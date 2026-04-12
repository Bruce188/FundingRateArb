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
    private static readonly ConcurrentDictionary<string, DateTime> Cooldowns = new();
    internal static readonly TimeSpan CooldownPeriod = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);
    private static long _lastPurgeTicks;

    /// <summary>
    /// Clears the cooldown cache. Used by unit tests to prevent cross-test interference.
    /// </summary>
    internal static void ClearCooldowns() => Cooldowns.Clear();

    /// <summary>
    /// Seeds a cooldown entry. Used by unit tests to exercise concurrent cooldown paths.
    /// </summary>
    internal static void SeedCooldown(string targetUserId, int exchangeId, DateTime timestamp)
        => Cooldowns[$"{targetUserId}|{exchangeId}"] = timestamp;

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
        string adminUserId, string targetUserId, int exchangeId, bool dryRun = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        // Periodic purge: remove all expired cooldown entries to prevent unbounded growth
        var now = DateTime.UtcNow;
        var nowTicks = now.Ticks;
        var previousTicks = Interlocked.Read(ref _lastPurgeTicks);
        if (nowTicks - previousTicks > PurgeInterval.Ticks
            && Interlocked.CompareExchange(ref _lastPurgeTicks, nowTicks, previousTicks) == previousTicks)
        {
            foreach (var kvp in Cooldowns)
            {
                if (now - kvp.Value >= CooldownPeriod)
                {
                    Cooldowns.TryRemove(kvp.Key, out _);
                }
            }
        }

        // Rate-limit: enforce per-(targetUserId, exchangeId) cooldown using atomic CAS.
        // ExchangeName is "Unknown" here because the early check fires before the DB lookup
        // to avoid a wasted database round-trip on rate-limited requests.
        var cooldownKey = $"{targetUserId}|{exchangeId}";
        if (Cooldowns.TryGetValue(cooldownKey, out var lastRun))
        {
            if (now - lastRun < CooldownPeriod)
            {
                var remaining = CooldownPeriod - (now - lastRun);
                return new ConnectivityTestResult(false, "Unknown",
                    $"Rate limited — wait {(int)remaining.TotalSeconds}s before retesting this exchange",
                    Mode: dryRun ? "DryRun" : "LiveTrade");
            }

            // Lazy eviction: remove expired entry
            Cooldowns.TryRemove(cooldownKey, out _);
        }

        // Sequential DB lookups — EF Core DbContext is not thread-safe
        var exchange = await _uow.Exchanges.GetByIdAsync(exchangeId);
        var credential = await _uow.UserCredentials.GetByUserAndExchangeAsync(targetUserId, exchangeId);

        if (exchange is null)
        {
            return new ConnectivityTestResult(false, "Unknown", "Exchange not found",
                Mode: dryRun ? "DryRun" : "LiveTrade");
        }

        var exchangeName = exchange.Name;
        var mode = dryRun ? "DryRun" : "LiveTrade";

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
                return new ConnectivityTestResult(false, exchangeName, "Data-only exchange, no trading support", Mode: mode);
            }

            // Check credentials
            if (credential is null || !credential.IsActive)
            {
                await Log("No active credentials found for this user/exchange combination");
                return new ConnectivityTestResult(false, exchangeName, "No active credentials found", Mode: mode);
            }

            // Active-trade lock: block connectivity test if the bot has an open position
            // on the same (userId, exchangeId) pair to prevent interfering with live trades.
            var openPositions = await _uow.Positions.GetOpenByUserAsync(targetUserId);
            var hasOpenPosition = openPositions.Any(p =>
                p.LongExchangeId == exchangeId || p.ShortExchangeId == exchangeId);

            if (hasOpenPosition)
            {
                await Log("Cannot run connectivity test: bot has an open position on this exchange. Try again after the position is closed.");
                return new ConnectivityTestResult(false, exchangeName,
                    "Cannot run connectivity test: bot has an open position on this exchange. Try again after the position is closed.",
                    Mode: mode);
            }

            await Log("Decrypting credentials...");
            var decrypted = _userSettings.DecryptCredential(credential);

            // Cooldown gate before connector creation to prevent concurrent duplicate trades.
            // If the factory returns null, we remove the key so the slot isn't consumed.
            if (!Cooldowns.TryAdd(cooldownKey, now))
            {
                // Another concurrent request claimed the slot; re-check expiry
                if (Cooldowns.TryGetValue(cooldownKey, out var existingTs) && now - existingTs < CooldownPeriod)
                {
                    var remaining = CooldownPeriod - (now - existingTs);
                    return new ConnectivityTestResult(false, exchangeName,
                        $"Rate limited — wait {(int)remaining.TotalSeconds}s before retesting this exchange",
                        Mode: mode);
                }

                // Expired entry from concurrent path — overwrite
                Cooldowns[cooldownKey] = now;
            }

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
                // Remove cooldown entry so a failed factory call doesn't consume the slot
                Cooldowns.TryRemove(cooldownKey, out _);
                await Log("Failed to create connector - invalid credentials");
                return new ConnectivityTestResult(false, exchangeName, "Failed to create connector - invalid credentials", Mode: mode);
            }

            try
            {
                if (dryRun)
                {
                    return await RunDryRunPathAsync(connector, exchangeName, mode, Log, ct);
                }
                else
                {
                    return await RunLiveTradePathAsync(connector, exchangeName, mode, Log, ct);
                }
            }
            finally
            {
                if (connector is IAsyncDisposable ad)
                {
                    await ad.DisposeAsync();
                }
                else
                {
                    (connector as IDisposable)?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connectivity test failed for {Exchange}", exchangeName);
            await Log("Unexpected error during connectivity test");
            return new ConnectivityTestResult(false, exchangeName, "Unexpected error during connectivity test", Mode: mode);
        }
    }

    private async Task<ConnectivityTestResult> RunDryRunPathAsync(
        IExchangeConnector connector, string exchangeName, string mode,
        Func<string, Task> log, CancellationToken ct)
    {
        // Step 1 — Balance (verifies API auth and account access)
        await log("[ConnectivityTest] Dry-run Step 1: Checking available balance...");
        decimal balance;
        try
        {
            balance = await connector.GetAvailableBalanceAsync(ct);
            _logger.LogTrace("[ConnectivityTest] [{Exchange}] Balance: ${Balance:F2}", exchangeName, balance);
            await log($"Balance: ${balance:F2} USDC");

            if (balance == 0m)
            {
                await log("WARNING: Balance is $0.00 — the expected quote asset (USDT) may not be present in the API response. Check exchange asset naming.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ConnectivityTest] Balance check failed for {Exchange}", exchangeName);
            var msg = $"Balance check failed: {Truncate(ex.Message)}";
            await log(msg);
            return new ConnectivityTestResult(false, exchangeName, msg, Mode: mode);
        }

        // Step 2 — Mark price (verifies market data access)
        await log("[ConnectivityTest] Dry-run Step 2: Fetching ETH mark price...");
        decimal markPrice;
        try
        {
            markPrice = await connector.GetMarkPriceAsync("ETH", ct);
            _logger.LogTrace("[ConnectivityTest] [{Exchange}] ETH mark price: ${Price:F2}", exchangeName, markPrice);
            await log($"ETH mark price: ${markPrice:F2}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ConnectivityTest] Mark price fetch failed for {Exchange}", exchangeName);
            var msg = $"Mark price check failed: {Truncate(ex.Message)}";
            await log(msg);
            return new ConnectivityTestResult(false, exchangeName, msg, Mode: mode);
        }

        // Step 3 — Funding rates (verifies read-only data endpoint)
        await log("[ConnectivityTest] Dry-run Step 3: Fetching funding rates...");
        try
        {
            var rates = await connector.GetFundingRatesAsync(ct);
            _logger.LogTrace("[ConnectivityTest] [{Exchange}] Funding rates count: {Count}", exchangeName, rates?.Count ?? 0);
            await log($"Funding rates: {rates?.Count ?? 0} entries");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ConnectivityTest] Funding rate fetch failed for {Exchange}", exchangeName);
            var msg = $"Funding rate check failed: {Truncate(ex.Message)}";
            await log(msg);
            return new ConnectivityTestResult(false, exchangeName, msg, Mode: mode);
        }

        await log("[ConnectivityTest] PASS - Dry-run OK");
        return new ConnectivityTestResult(true, exchangeName,
            $"Dry-run OK: balance={balance:F2}, ETH mark={markPrice:F2}", Mode: mode);
    }

    private async Task<ConnectivityTestResult> RunLiveTradePathAsync(
        IExchangeConnector connector, string exchangeName, string mode,
        Func<string, Task> log, CancellationToken ct)
    {
        // Step 1 - Balance check
        const decimal testSizeUsdc = 10m;
        await log("[ConnectivityTest] Step 1: Checking available balance...");
        decimal balance;
        try
        {
            balance = await connector.GetAvailableBalanceAsync(ct);
            _logger.LogTrace("[ConnectivityTest] [{Exchange}] Balance: ${Balance:F2}", exchangeName, balance);
            await log($"[ConnectivityTest] Balance: ${balance:F2} USDC");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ConnectivityTest] Balance check failed for {Exchange}", exchangeName);
            var msg = $"Balance check failed: {Truncate(ex.Message)}";
            await log(msg);
            return new ConnectivityTestResult(false, exchangeName, msg, Mode: mode);
        }

        if (balance < testSizeUsdc)
        {
            var skipMsg = $"[ConnectivityTest] Skipping trade test — available balance ${balance:F2} below required ${testSizeUsdc:F2}";
            await log(skipMsg);
            return new ConnectivityTestResult(true, exchangeName,
                $"API connectivity OK (${balance:F2} USDC), trade test skipped (below ${testSizeUsdc:F2} minimum)",
                Mode: mode);
        }

        // Step 2 - Open position
        await log($"[ConnectivityTest] Step 2: Opening ${testSizeUsdc} ETH Long 1x position...");
        var openResult = await connector.PlaceMarketOrderAsync("ETH", Side.Long, testSizeUsdc, 1, ct);
        if (!openResult.Success)
        {
            _logger.LogWarning("[ConnectivityTest] Open failed for {Exchange}: {Error}", exchangeName, openResult.Error);
            var openMsg = $"[ConnectivityTest] Open failed: {Truncate(openResult.Error)}";
            await log(openMsg);
            return new ConnectivityTestResult(false, exchangeName, openMsg, Mode: mode);
        }
        _logger.LogDebug("[ConnectivityTest] Open succeeded for {Exchange}: OrderId={OrderId} Price={Price} Qty={Qty}",
            exchangeName, openResult.OrderId, openResult.FilledPrice, openResult.FilledQuantity);
        await log("[ConnectivityTest] Open SUCCESS");

        // Settlement uses the caller's token for early cancellation (caught below),
        // while close always uses CancellationToken.None to prevent request cancellation
        // (e.g., browser navigation) from stranding an open position.

        // Step 3 - Wait for settlement
        await log("[ConnectivityTest] Step 3: Waiting for settlement (5s)...");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[ConnectivityTest] Settlement wait cancelled for {Exchange} — proceeding to close", exchangeName);
            await log("[ConnectivityTest] Settlement wait cancelled — proceeding to close position...");
        }

        // Step 4 - Close position (with retry on failure)
        // Uses CancellationToken.None to ensure close is always attempted after open
        await log("[ConnectivityTest] Step 4: Closing position...");
        var closeResult = await connector.ClosePositionAsync("ETH", Side.Long, CancellationToken.None);
        if (!closeResult.Success)
        {
            _logger.LogWarning("[ConnectivityTest] Close attempt 1 failed for {Exchange}: {Error}", exchangeName, closeResult.Error);
            await log($"[ConnectivityTest] Close attempt 1 failed: {Truncate(closeResult.Error)} — retrying in 3 seconds...");
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

            closeResult = await connector.ClosePositionAsync("ETH", Side.Long, CancellationToken.None);
            if (!closeResult.Success)
            {
                _logger.LogError("[ConnectivityTest] Close attempt 2 failed for {Exchange}: {Error}. STRANDED POSITION.",
                    exchangeName, closeResult.Error);
                var strandedMsg = $"[ConnectivityTest] STRANDED POSITION — close failed: {Truncate(closeResult.Error)}. Manual intervention required!";
                await log(strandedMsg);
                return new ConnectivityTestResult(false, exchangeName, strandedMsg, Mode: mode);
            }
        }
        _logger.LogInformation("[ConnectivityTest] Close succeeded for {Exchange}: OrderId={OrderId}",
            exchangeName, closeResult.OrderId);
        await log("[ConnectivityTest] Close SUCCESS");

        await log("[ConnectivityTest] PASS - All steps completed successfully");
        return new ConnectivityTestResult(true, exchangeName, null, null, mode);
    }

    private static string Truncate(string? s, int max = 500) =>
        s is null ? "(no details)" : s.Length > max ? s[..max] + "..." : s;
}
