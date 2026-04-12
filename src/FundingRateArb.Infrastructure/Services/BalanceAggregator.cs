using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class BalanceAggregator : IBalanceAggregator
{
    internal const string CredentialsNotConfiguredMessage = "Credentials not configured";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AuthErrorBackoff = TimeSpan.FromMinutes(5);

    private readonly IUserSettingsService _userSettings;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BalanceAggregator> _logger;

    public BalanceAggregator(
        IUserSettingsService userSettings,
        IExchangeConnectorFactory connectorFactory,
        IMemoryCache cache,
        ILogger<BalanceAggregator> logger)
    {
        _userSettings = userSettings;
        _connectorFactory = connectorFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<BalanceSnapshotDto> GetBalanceSnapshotAsync(string userId, CancellationToken ct = default)
    {
        var cacheKey = $"balance:{userId}";

        if (_cache.TryGetValue<BalanceSnapshotDto>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var credentials = await _userSettings.GetActiveCredentialsAsync(userId);

        if (credentials.Count == 0)
        {
            var empty = new BalanceSnapshotDto { FetchedAt = DateTime.UtcNow };
            _cache.Set(cacheKey, empty, CacheTtl);
            return empty;
        }

        var balanceTasks = new List<(int ExchangeId, string ExchangeName, Task<decimal> BalanceTask)>();
        var balances = new List<ExchangeBalanceDto>();

        foreach (var cred in credentials)
        {
            var exchangeName = cred.Exchange?.Name;
            if (string.IsNullOrEmpty(exchangeName))
            {
                _logger.LogWarning("Credential {CredId} has no exchange name, skipping", cred.Id);
                continue;
            }

            // Skip credentials in auth-error backoff
            if (cred.LastError is not null
                && cred.LastErrorAt is not null
                && cred.LastError.Contains("API key invalid", StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cred.LastErrorAt.Value < AuthErrorBackoff)
            {
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = cred.ExchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = 0m,
                    ErrorMessage = cred.LastError,
                    FetchedAt = DateTime.UtcNow,
                });
                continue;
            }

            var decrypted = _userSettings.DecryptCredential(cred);
            var connector = await _connectorFactory.CreateForUserAsync(
                exchangeName, decrypted.ApiKey, decrypted.ApiSecret,
                decrypted.WalletAddress, decrypted.PrivateKey,
                decrypted.SubAccountAddress, decrypted.ApiKeyIndex);

            if (connector is null)
            {
                _logger.LogWarning("Could not create connector for {Exchange} (user {UserId})", exchangeName, userId);
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = cred.ExchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = 0m,
                    ErrorMessage = CredentialsNotConfiguredMessage,
                    FetchedAt = DateTime.UtcNow,
                });
                continue;
            }

            balanceTasks.Add((cred.ExchangeId, exchangeName, connector.GetAvailableBalanceAsync(ct)));
        }

        var now = DateTime.UtcNow;

        // Await all balance tasks, handling individual failures
        foreach (var (exchangeId, exchangeName, task) in balanceTasks)
        {
            try
            {
                var balance = await task;
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = exchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = balance,
                    FetchedAt = now,
                });

                // Clear credential error on success
                await _userSettings.UpdateCredentialErrorAsync(userId, exchangeId, null, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch balance from {Exchange} for user {UserId}, using 0",
                    exchangeName, userId);
                var sanitized = SanitizeErrorMessage(ex, exchangeName);
                balances.Add(new ExchangeBalanceDto
                {
                    ExchangeId = exchangeId,
                    ExchangeName = exchangeName,
                    AvailableUsdc = 0m,
                    ErrorMessage = sanitized,
                    FetchedAt = now,
                });

                // Persist auth errors to credential for backoff tracking
                if (sanitized.Contains("API key invalid", StringComparison.OrdinalIgnoreCase))
                {
                    await _userSettings.UpdateCredentialErrorAsync(userId, exchangeId, sanitized, ct);
                }
            }
        }

        var snapshot = new BalanceSnapshotDto
        {
            Balances = balances,
            TotalAvailableUsdc = balances.Where(b => b.ErrorMessage is null).Sum(b => b.AvailableUsdc),
            FetchedAt = now,
        };

        _cache.Set(cacheKey, snapshot, CacheTtl);
        return snapshot;
    }

    private static string SanitizeErrorMessage(Exception ex, string exchangeName) => ex switch
    {
        HttpRequestException => $"{exchangeName}: unreachable",
        InvalidOperationException when ex.Message == CredentialsNotConfiguredMessage => CredentialsNotConfiguredMessage,
        InvalidOperationException when ex.Message.Contains("Invalid API-key", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("-2015", StringComparison.Ordinal)
            || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            => $"{exchangeName}: API key invalid or expired",
        InvalidOperationException when ex.Message.Contains("No recognized quote asset", StringComparison.Ordinal)
            => $"{exchangeName}: no recognized quote asset (USDT/USDC/USD) found",
        _ => $"{exchangeName}: balance fetch failed",
    };
}
