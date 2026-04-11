using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class ConnectorLifecycleManager : IConnectorLifecycleManager
{
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IUserSettingsService _userSettings;
    private readonly ILeverageTierProvider _tierProvider;
    private readonly ILogger<ConnectorLifecycleManager> _logger;

    // Per-asset leverage limit cache to avoid redundant API calls within a trading cycle
    private readonly ConcurrentDictionary<(string Exchange, string Asset), (int MaxLeverage, DateTime Fetched)> _leverageCache = new();

    public ConnectorLifecycleManager(
        IExchangeConnectorFactory connectorFactory,
        IUserSettingsService userSettings,
        ILeverageTierProvider tierProvider,
        ILogger<ConnectorLifecycleManager> logger)
    {
        _connectorFactory = connectorFactory;
        _userSettings = userSettings;
        _tierProvider = tierProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates user-specific exchange connectors for the long and short exchanges
    /// using the user's stored API credentials.
    /// </summary>
    /// <remarks>
    /// Optimization opportunity: when the orchestrator processes N positions for the same user
    /// in one cycle, this method is called N times with redundant credential lookups and connector
    /// initializations. A per-cycle cache keyed by (userId, exchangeName) would avoid repeated work.
    /// </remarks>
    public async Task<(IExchangeConnector Long, IExchangeConnector Short, string? Error)> CreateUserConnectorsAsync(
        string userId, string longExchangeName, string shortExchangeName)
    {
        // N1: Guard against null/empty userId — legacy records or admin-initiated operations
        // could pass null, leading to silent failures in credential lookup
        if (string.IsNullOrEmpty(userId))
        {
            _logger.LogError("CreateUserConnectorsAsync called with null/empty userId for {LongExchange}/{ShortExchange}",
                longExchangeName, shortExchangeName);
            return (null!, null!, "User ID is required for credential-based connector creation");
        }

        var credentials = await _userSettings.GetActiveCredentialsAsync(userId);

        var longCred = credentials.FirstOrDefault(c =>
            string.Equals(c.Exchange?.Name, longExchangeName, StringComparison.OrdinalIgnoreCase));
        var shortCred = credentials.FirstOrDefault(c =>
            string.Equals(c.Exchange?.Name, shortExchangeName, StringComparison.OrdinalIgnoreCase));

        if (longCred is null)
        {
            _logger.LogWarning("No credentials found for {Exchange} (user {UserId})", longExchangeName, userId);
            return (null!, null!, $"No credentials found for {longExchangeName}");
        }

        if (shortCred is null)
        {
            _logger.LogWarning("No credentials found for {Exchange} (user {UserId})", shortExchangeName, userId);
            return (null!, null!, $"No credentials found for {shortExchangeName}");
        }

        // NB4: Decrypt credentials in tightly scoped blocks to minimize plaintext lifetime in memory.
        // Create connectors immediately after decryption so credentials don't linger.
        IExchangeConnector? longConnector = null;
        IExchangeConnector? shortConnector = null;

        try
        {
            // Decrypt + create long connector in tight scope
            {
                var longDecrypted = DecryptAndCreateConnectorArgs(longCred, longExchangeName, userId);
                if (longDecrypted.Error is not null)
                {
                    return (null!, null!, longDecrypted.Error);
                }

                longConnector = await _connectorFactory.CreateForUserAsync(
                    longExchangeName, longDecrypted.ApiKey, longDecrypted.ApiSecret,
                    longDecrypted.WalletAddress, longDecrypted.PrivateKey,
                    longDecrypted.SubAccountAddress, longDecrypted.ApiKeyIndex);
            }

            // Decrypt + create short connector in tight scope
            {
                var shortDecrypted = DecryptAndCreateConnectorArgs(shortCred, shortExchangeName, userId);
                if (shortDecrypted.Error is not null)
                {
                    await DisposeConnectorAsync(longConnector);
                    return (null!, null!, shortDecrypted.Error);
                }

                shortConnector = await _connectorFactory.CreateForUserAsync(
                    shortExchangeName, shortDecrypted.ApiKey, shortDecrypted.ApiSecret,
                    shortDecrypted.WalletAddress, shortDecrypted.PrivateKey,
                    shortDecrypted.SubAccountAddress, shortDecrypted.ApiKeyIndex);
            }
        }
        catch (Exception ex)
        {
            // NB6: If CreateForUserAsync throws (not returns null), ensure cleanup
            _logger.LogError(ex, "Failed to create connector for user {UserId}", userId);
            await DisposeConnectorAsync(longConnector);
            await DisposeConnectorAsync(shortConnector);
            return (null!, null!, "Exchange connection failed");
        }

        if (longConnector is null)
        {
            await DisposeConnectorAsync(shortConnector);
            _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — invalid credentials", longExchangeName, userId);
            return (null!, null!, $"Could not create connector for {longExchangeName} — invalid credentials");
        }

        if (shortConnector is null)
        {
            await DisposeConnectorAsync(longConnector);
            _logger.LogWarning("Could not create connector for {Exchange} (user {UserId}) — invalid credentials", shortExchangeName, userId);
            return (null!, null!, $"Could not create connector for {shortExchangeName} — invalid credentials");
        }

        try
        {
            var longExchangeId = longCred.ExchangeId;
            var shortExchangeId = shortCred.ExchangeId;
            await _userSettings.TouchLastUsedAsync(userId, longExchangeId);
            await _userSettings.TouchLastUsedAsync(userId, shortExchangeId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TouchLastUsedAsync failed (non-fatal)");
        }

        return (longConnector, shortConnector, null);
    }

    public (IExchangeConnector Long, IExchangeConnector Short) WrapForDryRun(
        IExchangeConnector longConnector, IExchangeConnector shortConnector)
    {
        return (
            new DryRunConnectorWrapper(longConnector, _logger),
            new DryRunConnectorWrapper(shortConnector, _logger));
    }

    // NB4: Concurrent callers may trigger redundant fetches on cache miss — accepted
    // because duplicate writes produce the same value and the cost is bounded by the 1-hour TTL.
    public async Task<int?> GetCachedMaxLeverageAsync(IExchangeConnector connector, string asset, CancellationToken ct)
    {
        var key = (connector.ExchangeName, asset);
        if (_leverageCache.TryGetValue(key, out var cached) && cached.Fetched > DateTime.UtcNow.AddMinutes(-60))
        {
            return cached.MaxLeverage;
        }

        var maxLev = await connector.GetMaxLeverageAsync(asset, ct);
        if (maxLev.HasValue)
        {
            _leverageCache[key] = (maxLev.Value, DateTime.UtcNow);
        }

        return maxLev;
    }

    public async Task EnsureTiersCachedAsync(IExchangeConnector connector, string asset, CancellationToken ct)
    {
        if (!_tierProvider.IsStale(connector.ExchangeName, asset))
        {
            return;
        }

        var tiers = await connector.GetLeverageTiersAsync(asset, ct);
        if (tiers is { Length: > 0 })
        {
            _tierProvider.UpdateTiers(connector.ExchangeName, asset, tiers);
        }
    }

    /// <summary>
    /// Disposes a connector, checking for IAsyncDisposable first, then IDisposable.
    /// </summary>
    internal static async Task DisposeConnectorAsync(IExchangeConnector? connector)
    {
        if (connector is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Decrypts credential and returns the raw values needed for connector creation.
    /// Isolates decryption in its own scope to minimize plaintext credential lifetime.
    /// Note: .NET strings are immutable — decrypted credentials persist in memory until GC.
    /// This is an inherent platform limitation; SecureString is deprecated and not supported by exchange SDKs.
    /// </summary>
    private (string? ApiKey, string? ApiSecret, string? WalletAddress, string? PrivateKey, string? SubAccountAddress, string? ApiKeyIndex, string? Error) DecryptAndCreateConnectorArgs(
        UserExchangeCredential cred, string exchangeName, string userId)
    {
        try
        {
            var decrypted = _userSettings.DecryptCredential(cred);
            return (decrypted.ApiKey, decrypted.ApiSecret, decrypted.WalletAddress, decrypted.PrivateKey, decrypted.SubAccountAddress, decrypted.ApiKeyIndex, null);
        }
        catch (Exception ex)
        {
            // N2: Log only the exception type name, not the full exception which may contain cryptographic metadata
            _logger.LogError("Failed to decrypt credentials for {Exchange} (user {UserId}): {ExceptionType}",
                exchangeName, userId, ex.GetType().Name);
            return (null, null, null, null, null, null, "Credential validation failed");
        }
    }
}
