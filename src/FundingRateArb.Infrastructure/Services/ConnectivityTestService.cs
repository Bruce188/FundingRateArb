using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class ConnectivityTestService : IConnectivityTestService
{
    // Static: cooldown state must survive scope boundaries (service is Scoped)
    private static readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();
    internal static readonly TimeSpan CooldownPeriod = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);
    private static DateTime _lastPurge = DateTime.MinValue;

    /// <summary>
    /// Clears the cooldown cache. Used by unit tests to prevent cross-test interference.
    /// </summary>
    internal static void ClearCooldowns() => _cooldowns.Clear();

    private readonly IUserSettingsService _userSettings;
    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IUnitOfWork _uow;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hub;
    private readonly ILogger<ConnectivityTestService> _logger;

    public ConnectivityTestService(
        IUserSettingsService userSettings,
        IExchangeConnectorFactory connectorFactory,
        IUnitOfWork uow,
        IHubContext<DashboardHub, IDashboardClient> hub,
        ILogger<ConnectivityTestService> logger)
    {
        _userSettings = userSettings;
        _connectorFactory = connectorFactory;
        _uow = uow;
        _hub = hub;
        _logger = logger;
    }

    public async Task<ConnectivityTestResult> RunTestAsync(
        string adminUserId, string targetUserId, int exchangeId, CancellationToken ct = default)
    {
        // Periodic purge: remove all expired cooldown entries to prevent unbounded growth
        var now = DateTime.UtcNow;
        if (now - _lastPurge > PurgeInterval)
        {
            _lastPurge = now;
            foreach (var kvp in _cooldowns)
            {
                if (now - kvp.Value >= CooldownPeriod)
                    _cooldowns.TryRemove(kvp.Key, out _);
            }
        }

        // Rate-limit: enforce per-(targetUserId, exchangeId) cooldown using atomic CAS
        var cooldownKey = $"{targetUserId}:{exchangeId}";
        if (_cooldowns.TryGetValue(cooldownKey, out var lastRun))
        {
            if (now - lastRun < CooldownPeriod)
            {
                var remaining = CooldownPeriod - (now - lastRun);
                return new ConnectivityTestResult(false, "Unknown",
                    $"Rate limited — wait {(int)remaining.TotalSeconds}s before retesting this exchange");
            }

            // Lazy eviction: remove expired entry
            _cooldowns.TryRemove(cooldownKey, out _);
        }

        // Parallelize independent DB lookups
        var exchangeTask = _uow.Exchanges.GetByIdAsync(exchangeId);
        var credentialTask = _uow.UserCredentials.GetByUserAndExchangeAsync(targetUserId, exchangeId);
        await Task.WhenAll(exchangeTask, credentialTask);

        var exchange = await exchangeTask;
        var credential = await credentialTask;

        if (exchange is null)
        {
            return new ConnectivityTestResult(false, "Unknown", "Exchange not found");
        }

        var exchangeName = exchange.Name;

        async Task Log(string msg)
        {
            _logger.LogInformation("[ConnectivityTest] [{Exchange}] {Message}", exchangeName, msg);
            await _hub.Clients.Group($"user-{adminUserId}")
                .ReceiveConnectivityLog(exchangeName, msg);
        }

        try
        {
            if (exchange.IsDataOnly)
            {
                await Log("Skipped: data-only exchange, no trading support");
                return new ConnectivityTestResult(false, exchangeName, "Data-only exchange, no trading support");
            }

            // Check credentials
            if (credential is null || !credential.IsActive)
            {
                await Log("No active credentials found for this user/exchange combination");
                return new ConnectivityTestResult(false, exchangeName, "No active credentials found");
            }

            await Log("Decrypting credentials...");
            var decrypted = _userSettings.DecryptCredential(credential);

            await Log("Creating exchange connector...");
            var connector = await _connectorFactory.CreateForUserAsync(
                exchangeName,
                decrypted.ApiKey,
                decrypted.ApiSecret,
                decrypted.WalletAddress,
                decrypted.PrivateKey,
                decrypted.SubAccountAddress,
                decrypted.ApiKeyIndex);

            if (connector is null)
            {
                await Log("Failed to create connector - invalid credentials");
                return new ConnectivityTestResult(false, exchangeName, "Failed to create connector - invalid credentials");
            }

            // Record cooldown timestamp atomically — only one concurrent caller proceeds
            if (!_cooldowns.TryAdd(cooldownKey, now))
            {
                // Another concurrent request claimed the slot; re-check expiry
                if (_cooldowns.TryGetValue(cooldownKey, out var existingTs) && now - existingTs < CooldownPeriod)
                {
                    var remaining = CooldownPeriod - (now - existingTs);
                    return new ConnectivityTestResult(false, exchangeName,
                        $"Rate limited — wait {(int)remaining.TotalSeconds}s before retesting this exchange");
                }

                // Expired entry from concurrent path — overwrite
                _cooldowns[cooldownKey] = now;
            }

            // Step 1 - Balance check
            await Log("Step 1: Checking available balance...");
            decimal balance;
            try
            {
                balance = await connector.GetAvailableBalanceAsync(ct);
                _logger.LogInformation("[ConnectivityTest] [{Exchange}] Balance: ${Balance:F2}", exchangeName, balance);
                await Log("Balance check OK");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Balance check failed for {Exchange}", exchangeName);
                await Log("Balance check failed — see server log for details");
                return new ConnectivityTestResult(false, exchangeName, "Balance check failed — see server log for details");
            }

            // Step 2 - Open position
            await Log("Step 2: Opening $5 ETH Long 1x position...");
            var openResult = await connector.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, ct);
            if (!openResult.Success)
            {
                _logger.LogWarning("Open failed for {Exchange}: {Error}", exchangeName, openResult.Error);
                await Log("Open failed — see server log for details");
                return new ConnectivityTestResult(false, exchangeName, "Open failed — see server log for details", balance);
            }
            _logger.LogInformation("Open succeeded for {Exchange}: OrderId={OrderId} Price={Price} Qty={Qty}",
                exchangeName, openResult.OrderId, openResult.FilledPrice, openResult.FilledQuantity);
            await Log("Open SUCCESS");

            // Step 3 - Wait for settlement
            await Log("Step 3: Waiting for settlement (2s)...");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // Step 4 - Close position (with retry on failure)
            await Log("Step 4: Closing position...");
            var closeResult = await connector.ClosePositionAsync("ETH", Side.Long, ct);
            if (!closeResult.Success)
            {
                _logger.LogWarning("Close attempt 1 failed for {Exchange}: {Error}", exchangeName, closeResult.Error);
                await Log("Close failed — retrying in 3 seconds...");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);

                closeResult = await connector.ClosePositionAsync("ETH", Side.Long, ct);
                if (!closeResult.Success)
                {
                    _logger.LogError("Close attempt 2 failed for {Exchange}: {Error}. STRANDED POSITION.",
                        exchangeName, closeResult.Error);
                    await Log("STRANDED POSITION — close retry failed. Manual intervention required!");
                    return new ConnectivityTestResult(false, exchangeName,
                        "STRANDED POSITION — close failed after retry. Manual intervention required!", balance);
                }
            }
            _logger.LogInformation("Close succeeded for {Exchange}: OrderId={OrderId}",
                exchangeName, closeResult.OrderId);
            await Log("Close SUCCESS");

            await Log("PASS - All steps completed successfully");
            return new ConnectivityTestResult(true, exchangeName, null, balance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connectivity test failed for {Exchange}", exchangeName);
            await Log("Unexpected error during connectivity test");
            return new ConnectivityTestResult(false, exchangeName, "Unexpected error during connectivity test");
        }
    }
}
