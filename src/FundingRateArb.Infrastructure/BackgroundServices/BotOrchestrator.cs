using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class BotOrchestrator : BackgroundService, IBotControl
{
    // M2: Instance-level semaphore (not static) — avoids cross-instance interference in tests
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private bool _disposed;

    // C6: CancellationTokenSource replaces _immediateRunRequested bool — cancels the timer wait immediately
    private CancellationTokenSource _immediateCts = new();

    // H6: Track pushed alert IDs to prevent duplicate pushes across cycles
    private readonly ConcurrentDictionary<int, byte> _pushedAlertIds = new();

    // Per-user consecutive loss tracking for circuit breaker
    private readonly ConcurrentDictionary<string, int> _userConsecutiveLosses = new();

    // M4: Extract magic polling intervals to named constants
    private const int CycleIntervalSeconds = 60;

    // M7: Track when alerts were last pushed so each cycle uses only the elapsed window (no replays)
    private DateTime _lastAlertPushUtc = DateTime.UtcNow.AddMinutes(-5);

    // Per-user cooldown for failed opportunities — keyed by (userId, oppKey)
    private readonly ConcurrentDictionary<string, (DateTime CooldownUntil, int Failures)> _failedOpCooldowns = new();
    internal static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(60);

    // Per-exchange circuit breaker — keyed by exchangeId
    private readonly ConcurrentDictionary<int, (int Failures, DateTime BrokenUntil)> _exchangeCircuitBreaker = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFundingRateReadinessSignal _readinessSignal;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hubContext;
    private readonly ILogger<BotOrchestrator> _logger;

    public BotOrchestrator(
        IServiceScopeFactory scopeFactory,
        IFundingRateReadinessSignal readinessSignal,
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        ILogger<BotOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _readinessSignal = readinessSignal;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for first FundingRateFetcher cycle to complete (with 120s timeout)
        await _readinessSignal.WaitForReadyAsync(ct);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CycleIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            // C6: Use linked CTS so TriggerImmediateCycle() cancels the timer wait instantly
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _immediateCts.Token);
                if (!await timer.WaitForNextTickAsync(linkedCts.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (_immediateCts.IsCancellationRequested)
            {
                // Immediate cycle requested — reset the CTS for next use
                _immediateCts.Dispose();
                _immediateCts = new CancellationTokenSource();
            }
            // Belt-and-suspenders: skip if previous cycle still running
            if (!await _cycleLock.WaitAsync(0, ct))
            {
                // L1: Routine overlap — Debug to avoid polluting the SQL audit sink
                _logger.LogDebug("Previous bot cycle still running — skipping this tick");
                continue;
            }

            try
            {
                await RunCycleAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bot cycle failed unexpectedly");
            }
            finally
            {
                _cycleLock.Release();
            }
        }
    }

    // IBotControl implementation
    public void ClearCooldowns() => _failedOpCooldowns.Clear();
    // C6: Cancel the timer wait to trigger an immediate cycle
    public void TriggerImmediateCycle() => _immediateCts.Cancel();

    private void IncrementExchangeFailure(int exchangeId, BotConfiguration config)
    {
        var threshold = config.ExchangeCircuitBreakerThreshold;
        var brokenUntil = DateTime.UtcNow.AddMinutes(config.ExchangeCircuitBreakerMinutes);

        var updated = _exchangeCircuitBreaker.AddOrUpdate(
            exchangeId,
            _ =>
            {
                var f = 1;
                return (f, f >= threshold ? brokenUntil : DateTime.MinValue);
            },
            (_, current) =>
            {
                var f = current.Failures + 1;
                return (f, f >= threshold ? brokenUntil : DateTime.MinValue);
            });

        if (updated.Failures >= threshold)
        {
            _logger.LogWarning(
                "Circuit breaker OPEN for exchange {ExchangeId}: {Failures} consecutive failures, excluded for {Minutes}m",
                exchangeId, updated.Failures, config.ExchangeCircuitBreakerMinutes);
        }
    }

    public void RecordCloseResult(decimal realizedPnl, string? userId = null)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        if (realizedPnl < 0)
        {
            _userConsecutiveLosses.AddOrUpdate(userId, 1, (_, count) => count + 1);
        }
        else
        {
            _userConsecutiveLosses[userId] = 0;
        }
    }

    /// <summary>Exposes cooldown state for unit testing.</summary>
    internal ConcurrentDictionary<string, (DateTime CooldownUntil, int Failures)> FailedOpCooldowns => _failedOpCooldowns;

    /// <summary>Exposes per-user consecutive loss counts for unit testing.</summary>
    internal ConcurrentDictionary<string, int> UserConsecutiveLosses => _userConsecutiveLosses;

    /// <summary>Exposes pushed alert IDs for unit testing.</summary>
    internal ConcurrentDictionary<int, byte> PushedAlertIds => _pushedAlertIds;

    /// <summary>Exposes exchange circuit breaker state for unit testing.</summary>
    internal ConcurrentDictionary<int, (int Failures, DateTime BrokenUntil)> ExchangeCircuitBreaker => _exchangeCircuitBreaker;

    /// <summary>
    /// One bot cycle: health-monitor ALL open positions, then iterate enabled users.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        // Sweep expired circuit breaker entries. Sub-threshold entries (BrokenUntil == DateTime.MinValue)
        // are not swept here — they are bounded by the finite set of exchange IDs and acceptable.
        var expiredCbKeys = _exchangeCircuitBreaker
            .Where(kvp => kvp.Value.BrokenUntil < DateTime.UtcNow && kvp.Value.BrokenUntil != DateTime.MinValue)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expiredCbKeys)
        {
            _exchangeCircuitBreaker.TryRemove(key, out _);
        }

        // Sweep stale per-opportunity cooldown entries to prevent unbounded dictionary growth.
        // Only remove entries that expired more than MaxCooldown ago, preserving failure counts
        // for recently-expired entries so exponential backoff continues correctly on retry.
        var cooldownSweepThreshold = DateTime.UtcNow - MaxCooldown;
        var expiredCooldownKeys = _failedOpCooldowns
            .Where(kvp => kvp.Value.CooldownUntil < cooldownSweepThreshold)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expiredCooldownKeys)
        {
            _failedOpCooldowns.TryRemove(key, out _);
        }

        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var globalConfig = await uow.BotConfig.GetActiveAsync();
        var healthMonitor = scope.ServiceProvider.GetRequiredService<IPositionHealthMonitor>();
        var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
        var executionEngine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        var userSettings = scope.ServiceProvider.GetRequiredService<IUserSettingsService>();

        // Step 1: Always run health monitor for ALL open positions (regardless of user or bot state)
        var healthResult = await healthMonitor.CheckAndActAsync(ct);
        var closedPositionIds = new List<(int PositionId, string UserId)>();
        foreach (var (pos, reason) in healthResult.ToClose)
        {
            await executionEngine.ClosePositionAsync(pos.UserId, pos, reason, ct);
            if (pos.Status == PositionStatus.Closed && pos.RealizedPnl.HasValue)
            {
                RecordCloseResult(pos.RealizedPnl.Value, pos.UserId);
                closedPositionIds.Add((pos.Id, pos.UserId));
            }
        }

        // Push removal events for reaped and closed positions
        await PushPositionRemovalsAsync(healthResult.ReapedPositions, closedPositionIds);

        // Fetch ALL open positions after health monitor so closed positions are excluded
        var allOpenPositions = await uow.Positions.GetOpenAsync();

        // Push per-user position updates with warning computation (always, regardless of bot state)
        await PushPositionUpdatesAsync(allOpenPositions, globalConfig);
        await PushNewAlertsAsync(uow);

        // Step 2: Compute + push opportunities globally (always, regardless of bot state)
        var opportunityResult = await signalEngine.GetOpportunitiesWithDiagnosticsAsync(ct);
        var allOpportunities = opportunityResult.Opportunities;
        try
        {
            await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveOpportunityUpdate(opportunityResult);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push opportunity update via SignalR");
        }

        // Step 2b: Portfolio rebalancing — close positions that should be replaced by better opportunities
        // Uses already-fetched positions and opportunities to avoid duplicate signal engine calls
        if (globalConfig.RebalanceEnabled)
        {
            try
            {
                var rebalancer = scope.ServiceProvider.GetRequiredService<IPortfolioRebalancer>();
                var recommendations = await rebalancer.EvaluateAsync(allOpenPositions, allOpportunities, globalConfig, ct);

                var closedIds = new HashSet<int>();
                foreach (var rec in recommendations)
                {
                    if (closedIds.Count >= globalConfig.MaxRebalancesPerCycle)
                    {
                        _logger.LogInformation(
                            "Rebalancing: per-cycle cap ({Cap}) reached, skipping remaining {Remaining} recommendations",
                            globalConfig.MaxRebalancesPerCycle, recommendations.Count - closedIds.Count);
                        break;
                    }

                    var posToClose = allOpenPositions.FirstOrDefault(p => p.Id == rec.PositionId);
                    if (posToClose is not null)
                    {
                        _logger.LogInformation(
                            "Rebalancing: closing position #{PositionId} ({Asset}) for better opportunity {NewAsset} on {NewLong}/{NewShort}",
                            rec.PositionId, rec.PositionAsset, rec.ReplacementAsset,
                            rec.ReplacementLongExchange, rec.ReplacementShortExchange);
                        await executionEngine.ClosePositionAsync(posToClose.UserId, posToClose, CloseReason.Rebalanced, ct);
                        // Only track as closed if the position actually transitioned
                        if (posToClose.Status is PositionStatus.Closed or PositionStatus.Closing or PositionStatus.EmergencyClosed)
                        {
                            closedIds.Add(rec.PositionId);
                        }
                    }
                }

                // Push removal events for rebalanced positions
                if (closedIds.Count > 0)
                {
                    var rebalancedRemovals = allOpenPositions
                        .Where(p => closedIds.Contains(p.Id))
                        .Select(p => (p.Id, p.UserId))
                        .ToList();
                    var removalTasks = rebalancedRemovals.Select(async r =>
                    {
                        try
                        {
                            await _hubContext.Clients.Group($"user-{r.UserId}").ReceivePositionRemoval(r.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to push rebalance removal for #{PositionId}", r.Id);
                        }
                    });
                    await Task.WhenAll(removalTasks);

                    // Filter only actually closed positions so unclosed ones still get health monitoring
                    allOpenPositions = allOpenPositions.Where(p => !closedIds.Contains(p.Id)).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Portfolio rebalancing failed — continuing with normal cycle");
            }
        }

        // Step 3: Push global dashboard KPI update
        await PushDashboardUpdateAsync(allOpenPositions, allOpportunities, globalConfig.IsEnabled);

        // Step 4: Gate — skip position opening if global kill switch is off
        if (!globalConfig.IsEnabled)
        {
            _logger.LogDebug("Bot is disabled (kill switch). Skipping cycle.");
            await PushStatusExplanationAsync(null, "Bot is disabled — enable in Settings or Admin > Bot Config", "danger");
            return;
        }

        // Step 5: Get all users with enabled UserConfiguration and iterate
        var enabledUserIds = await uow.UserConfigurations.GetAllEnabledUserIdsAsync();

        if (enabledUserIds.Count == 0)
        {
            _logger.LogDebug("No users with enabled bot configuration. Skipping cycle.");
            return;
        }

        // Track skip reasons in a single tracker object
        var tracker = new SkipReasonTracker();

        // Fetch data-only exchange IDs once per cycle (same for all users)
        var dataOnlyExchangeIds = (await userSettings.GetDataOnlyExchangeIdsAsync()).ToHashSet();

        // Pre-compute circuit-broken exchange IDs once before the user loop
        var circuitBrokenExchangeIds = _exchangeCircuitBreaker
            .Where(kvp => kvp.Value.BrokenUntil > DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        // Track known opportunity keys for adaptive candidate dedup (value-based, no reference equality)
        var knownOpportunityKeys = new HashSet<string>(allOpportunities.Select(OpportunityKey));

        // Create a local snapshot list for adaptive appends so we don't mutate the signal engine's DTO
        var snapshotOpportunities = new List<ArbitrageOpportunityDto>(allOpportunities);

        foreach (var userId in enabledUserIds)
        {
            // Clear per-user skip reason sets to prevent cross-user leakage
            tracker.ClearPerUserSets();
            try
            {
                await ExecuteUserCycleAsync(userId, globalConfig, opportunityResult, allOpenPositions, uow, signalEngine, executionEngine, userSettings, dataOnlyExchangeIds, circuitBrokenExchangeIds, knownOpportunityKeys, snapshotOpportunities, tracker, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cycle failed for user {UserId}", userId);
            }
        }

        // Step 6: Persist opportunity snapshots and run 7-day purge
        await PersistOpportunitySnapshotsAsync(uow, snapshotOpportunities, allOpenPositions, tracker, ct);
    }

    /// <summary>
    /// Per-user cycle: filter opportunities, check gates, size and open positions.
    /// </summary>
    private async Task ExecuteUserCycleAsync(
        string userId,
        BotConfiguration globalConfig,
        OpportunityResultDto opportunityResult,
        List<ArbitragePosition> allOpenPositions,
        IUnitOfWork uow,
        ISignalEngine signalEngine,
        IExecutionEngine executionEngine,
        IUserSettingsService userSettings,
        HashSet<int> dataOnlyExchangeIds,
        HashSet<int> circuitBrokenExchangeIds,
        HashSet<string> knownOpportunityKeys,
        List<ArbitrageOpportunityDto> snapshotOpportunities,
        SkipReasonTracker tracker,
        CancellationToken ct)
    {
        var allOpportunities = opportunityResult.Opportunities;
        var userConfig = await userSettings.GetOrCreateConfigAsync(userId);
        if (!userConfig.IsEnabled)
        {
            return;
        }

        // Check user has valid credentials (at least 2 exchanges)
        if (!await userSettings.HasValidCredentialsAsync(userId))
        {
            await PushStatusExplanationAsync(userId, "No exchange API keys configured — set up in Settings > API Keys", "danger");
            return;
        }

        // Get user's enabled exchange and asset IDs
        var enabledExchangeIds = await userSettings.GetUserEnabledExchangeIdsAsync(userId);
        var enabledAssetIds = await userSettings.GetUserEnabledAssetIdsAsync(userId);

        var enabledExchangeSet = enabledExchangeIds.ToHashSet();
        var enabledAssetSet = enabledAssetIds.ToHashSet();

        // Filter global opportunities to user's preferences, excluding data-only and circuit-broken exchanges
        // Track specific skip reasons for each filtered opportunity
        var userOpportunities = new List<ArbitrageOpportunityDto>();
        foreach (var o in allOpportunities)
        {
            var key = OpportunityKey(o);

            if (!enabledExchangeSet.Contains(o.LongExchangeId) || !enabledExchangeSet.Contains(o.ShortExchangeId))
            {
                tracker.ExchangeDisabledKeys.Add(key);
                continue;
            }
            if (dataOnlyExchangeIds.Contains(o.LongExchangeId) || dataOnlyExchangeIds.Contains(o.ShortExchangeId))
            {
                tracker.ExchangeDisabledKeys.Add(key);
                continue;
            }
            if (circuitBrokenExchangeIds.Contains(o.LongExchangeId) || circuitBrokenExchangeIds.Contains(o.ShortExchangeId))
            {
                tracker.CircuitBrokenKeys.Add(key);
                continue;
            }
            if (!enabledAssetSet.Contains(o.AssetId))
            {
                tracker.AssetDisabledKeys.Add(key);
                continue;
            }
            if (o.NetYieldPerHour < userConfig.OpenThreshold)
            {
                // Will be handled by the "below_threshold" fallback in snapshot persistence
                continue;
            }
            userOpportunities.Add(o);
        }

        // Check user's open positions vs their limit
        var userOpenPositions = allOpenPositions.Where(p => p.UserId == userId).ToList();

        // Daily drawdown circuit breaker for this user
        var closedToday = await uow.Positions.GetClosedSinceAsync(DateTime.UtcNow.Date);
        var userClosedToday = closedToday.Where(p => p.UserId == userId).ToList();
        var dailyPnl = userClosedToday.Sum(p => p.RealizedPnl ?? 0m) + userOpenPositions.Sum(p => p.AccumulatedFunding);
        var drawdownLimit = userConfig.TotalCapitalUsdc * userConfig.DailyDrawdownPausePct;
        if (dailyPnl < -drawdownLimit)
        {
            _logger.LogWarning(
                "Daily drawdown limit hit for user {UserId}: {DailyPnl:F2} USDC (limit: -{Limit:F2})",
                userId, dailyPnl, drawdownLimit);
            await PushStatusExplanationAsync(userId,
                $"Daily drawdown limit hit ({dailyPnl:F2} USDC, limit: -{drawdownLimit:F2}) — pausing position opens", "warning");
            return;
        }

        // Consecutive loss circuit breaker for this user
        var consecutiveLosses = _userConsecutiveLosses.GetValueOrDefault(userId, 0);
        if (consecutiveLosses >= userConfig.ConsecutiveLossPause)
        {
            _logger.LogWarning(
                "Consecutive loss limit reached for user {UserId} ({Count}/{Max})",
                userId, consecutiveLosses, userConfig.ConsecutiveLossPause);
            await PushStatusExplanationAsync(userId,
                $"Consecutive loss limit reached ({consecutiveLosses}/{userConfig.ConsecutiveLossPause}) — pausing position opens", "warning");
            return;
        }

        // Max positions gate
        if (userOpenPositions.Count >= userConfig.MaxConcurrentPositions)
        {
            _logger.LogDebug(
                "Max concurrent positions reached for user {UserId} ({Count}/{Max})",
                userId, userOpenPositions.Count, userConfig.MaxConcurrentPositions);
            await PushStatusExplanationAsync(userId,
                $"{userOpenPositions.Count}/{userConfig.MaxConcurrentPositions} position slots occupied", "info");
            return;
        }

        // Filter candidates: exclude active/opening positions and cooled-down opportunities
        var openingPositions = await uow.Positions.GetByStatusAsync(PositionStatus.Opening);
        var userOpeningPositions = openingPositions.Where(p => p.UserId == userId).ToList();
        var allActiveKeys = userOpenPositions
            .Concat(userOpeningPositions)
            .Select(PositionKey)
            .ToHashSet();

        var cooldownSkips = new List<(string Asset, TimeSpan Remaining)>();

        var filteredCandidates = userOpportunities
            .Where(opp =>
            {
                var key = OpportunityKey(opp);
                if (allActiveKeys.Contains(key))
                {
                    return false;
                }
                // Per-user cooldown key
                var cooldownKey = $"{userId}:{key}";
                if (_failedOpCooldowns.TryGetValue(cooldownKey, out var cd) && DateTime.UtcNow < cd.CooldownUntil)
                {
                    _logger.LogDebug(
                        "Skipping {Asset} {Long}/{Short} for user {UserId} — on cooldown until {Until}",
                        opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, userId, cd.CooldownUntil);
                    cooldownSkips.Add((opp.AssetSymbol, cd.CooldownUntil - DateTime.UtcNow));
                    tracker.CooldownKeys.Add(key);
                    return false;
                }
                return true;
            })
            .ToList();

        var takeCount = userConfig.AllocationStrategy == AllocationStrategy.Concentrated ? 1 : userConfig.AllocationTopN;
        var candidates = filteredCandidates.Take(takeCount).ToList();

        // Track opportunities that passed all filters but weren't selected by allocation strategy
        foreach (var opp in filteredCandidates.Skip(takeCount))
        {
            tracker.NotSelectedKeys.Add(OpportunityKey(opp));
        }

        if (candidates.Count == 0)
        {
            // Adaptive threshold fallback: when user has 0 open positions, try net-positive opportunities below threshold
            if (userOpenPositions.Count == 0 && opportunityResult.AllNetPositive.Count > 0)
            {
                var adaptiveCandidates = opportunityResult.AllNetPositive
                    .Where(o => enabledExchangeSet.Contains(o.LongExchangeId) && enabledExchangeSet.Contains(o.ShortExchangeId))
                    .Where(o => !dataOnlyExchangeIds.Contains(o.LongExchangeId) && !dataOnlyExchangeIds.Contains(o.ShortExchangeId))
                    .Where(o => !circuitBrokenExchangeIds.Contains(o.LongExchangeId) && !circuitBrokenExchangeIds.Contains(o.ShortExchangeId))
                    .Where(o => enabledAssetSet.Contains(o.AssetId))
                    .Where(opp =>
                    {
                        var key = OpportunityKey(opp);
                        if (allActiveKeys.Contains(key))
                        {
                            return false;
                        }

                        var cooldownKey = $"{userId}:{key}";
                        if (_failedOpCooldowns.TryGetValue(cooldownKey, out var cd) && DateTime.UtcNow < cd.CooldownUntil)
                        {
                            tracker.CooldownKeys.Add(key);
                            return false;
                        }

                        return true;
                    })
                    .Take(1)
                    .ToList();

                if (adaptiveCandidates.Count > 0)
                {
                    var bestAdaptive = adaptiveCandidates[0];
                    _logger.LogInformation(
                        "Adaptive threshold for user {UserId}: {Asset} at {Net:F6}/hr (below normal threshold {Threshold:F6})",
                        userId, bestAdaptive.AssetSymbol, bestAdaptive.NetYieldPerHour, userConfig.OpenThreshold);
                    await PushStatusExplanationAsync(userId,
                        $"Adaptive threshold: opening {bestAdaptive.AssetSymbol} at {bestAdaptive.NetYieldPerHour * 100:F4}%/hr (below normal threshold)", "info");

                    // Use the normal execution path with the adaptive candidate
                    candidates = adaptiveCandidates;

                    // B1: Ensure adaptive candidate is tracked in snapshotOpportunities (not the signal engine's DTO)
                    // so PersistOpportunitySnapshotsAsync can record WasOpened = true instead of "below_threshold"
                    foreach (var ac in adaptiveCandidates)
                    {
                        if (knownOpportunityKeys.Add(OpportunityKey(ac)))
                        {
                            snapshotOpportunities.Add(ac);
                        }
                    }
                    // Fall through to sizing + execution below
                }
            }

            if (candidates.Count == 0)
            {
                if (userOpportunities.Count == 0 && allOpportunities.Count == 0)
                {
                    await PushStatusExplanationAsync(userId, "No arbitrage opportunities detected this cycle", "info");
                }
                else if (userOpportunities.Count == 0)
                {
                    await PushStatusExplanationAsync(userId, "No opportunities match your enabled exchanges and coins", "info");
                }
                else if (cooldownSkips.Count > 0)
                {
                    var first = cooldownSkips[0];
                    var minutes = (int)Math.Ceiling(first.Remaining.TotalMinutes);
                    await PushStatusExplanationAsync(userId,
                        $"{first.Asset} cooling down — retry in {minutes} minutes", "info");
                }
                else
                {
                    var bestOpp = userOpportunities.OrderByDescending(o => o.SpreadPerHour).First();
                    await PushStatusExplanationAsync(userId,
                        $"Best spread {(bestOpp.SpreadPerHour * 100):F2}%/hr — all opportunities have active positions", "info");
                }
                return;
            }
        }

        using var sizerScope = _scopeFactory.CreateScope();
        var positionSizer = sizerScope.ServiceProvider.GetRequiredService<IPositionSizer>();
        var balanceAggregator = sizerScope.ServiceProvider.GetRequiredService<IBalanceAggregator>();
        var sizes = await positionSizer.CalculateBatchSizesAsync(candidates, userConfig.AllocationStrategy, userId, ct);

        // Push balance snapshot to user's dashboard
        try
        {
            var balanceSnapshot = await balanceAggregator.GetBalanceSnapshotAsync(userId, ct);
            await _hubContext.Clients.Group($"user-{userId}").ReceiveBalanceUpdate(balanceSnapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push balance update for user {UserId}", userId);
        }
        var slotsAvailable = userConfig.MaxConcurrentPositions - userOpenPositions.Count - userOpeningPositions.Count;
        var positionsChanged = false;

        for (int idx = 0; idx < candidates.Count; idx++)
        {
            var opp = candidates[idx];
            var size = sizes[idx];
            if (size <= 0)
            {
                tracker.CapitalExhaustedKeys.Add(OpportunityKey(opp));
                continue;
            }

            if (slotsAvailable <= 0)
            {
                // Track remaining candidates as skipped due to max positions
                for (int remaining = idx; remaining < candidates.Count; remaining++)
                {
                    var remOpp = candidates[remaining];
                    tracker.MaxPositionsKeys.Add(OpportunityKey(remOpp));
                }
                break;
            }

            var key = OpportunityKey(opp);
            var cooldownKey = $"{userId}:{key}";

            _logger.LogInformation(
                "Opening position for user {UserId}: {Asset} {LongExchange}/{ShortExchange} size={Size} USDC",
                userId, opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, size);

            var (success, error) = await executionEngine.OpenPositionAsync(userId, opp, size, ct);

            if (success)
            {
                slotsAvailable--;
                _failedOpCooldowns.TryRemove(cooldownKey, out _);
                tracker.OpenedOppKeys.Add(key);
                positionsChanged = true;

                // Reset circuit breaker on success.
                // Note: TryRemove could race with concurrent IncrementExchangeFailure, but user
                // processing is sequential in the current design. If parallelism is introduced,
                // switch to AddOrUpdate with a conditional reset instead.
                _exchangeCircuitBreaker.TryRemove(opp.LongExchangeId, out _);
                _exchangeCircuitBreaker.TryRemove(opp.ShortExchangeId, out _);

                var msg = $"Opened position: {opp.AssetSymbol} {opp.LongExchangeName}/{opp.ShortExchangeName}";
                await _hubContext.Clients.Group($"user-{userId}").ReceiveNotification(msg);
                await _hubContext.Clients.Group(HubGroups.Admins).ReceiveNotification(msg);
                await PushNewAlertsAsync(uow);
            }
            else if (error != null && (error.Contains("Insufficient margin", StringComparison.OrdinalIgnoreCase)
                                       || error.Contains("balance", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Balance exhausted for user {UserId}: {Error}", userId, error);
                await PushStatusExplanationAsync(userId,
                    $"Insufficient balance on {opp.LongExchangeName}/{opp.ShortExchangeName} for {opp.AssetSymbol} (need {size:F2} USDC per exchange)", "warning");
                // Track remaining candidates as skipped due to capital exhaustion
                for (int remaining = idx + 1; remaining < candidates.Count; remaining++)
                {
                    var remOpp = candidates[remaining];
                    tracker.CapitalExhaustedKeys.Add(OpportunityKey(remOpp));
                }
                break;
            }
            else
            {
                var failures = _failedOpCooldowns.GetValueOrDefault(cooldownKey).Failures + 1;
                var delay = TimeSpan.FromTicks(
                    Math.Min(BaseCooldown.Ticks * (1L << Math.Min(failures - 1, 4)), MaxCooldown.Ticks));
                _failedOpCooldowns[cooldownKey] = (DateTime.UtcNow + delay, failures);

                // Increment circuit breaker for both exchanges involved
                IncrementExchangeFailure(opp.LongExchangeId, globalConfig);
                IncrementExchangeFailure(opp.ShortExchangeId, globalConfig);

                _logger.LogWarning(
                    "Opportunity {Asset} {Long}/{Short} failed for user {UserId} ({Failures} consecutive). Cooldown until {Until}",
                    opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, userId, failures, DateTime.UtcNow + delay);
                _logger.LogError("Failed to open position: {Error}", error);
                await PushNewAlertsAsync(uow);
            }
        }

        // NB4: Refresh open positions once after the entire user loop, not per-open
        if (positionsChanged)
        {
            var updatedPositions = await uow.Positions.GetOpenAsync();
            allOpenPositions.Clear();
            allOpenPositions.AddRange(updatedPositions);
            await PushPositionUpdatesAsync(updatedPositions, globalConfig);
        }
    }

    /// <summary>
    /// Persists opportunity snapshots for retrospective analysis.
    /// Determines SkipReason based on whether the opportunity was opened, had an active position, or was below threshold.
    /// </summary>
    private async Task PersistOpportunitySnapshotsAsync(
        IUnitOfWork uow,
        List<ArbitrageOpportunityDto> opportunities,
        List<ArbitragePosition> allOpenPositions,
        SkipReasonTracker tracker,
        CancellationToken ct)
    {
        try
        {
            if (opportunities.Count == 0)
            {
                return;
            }

            var activeKeys = allOpenPositions
                .Select(PositionKey)
                .ToHashSet();

            var snapshots = opportunities.Select(opp =>
            {
                var key = OpportunityKey(opp);
                var wasOpened = tracker.OpenedOppKeys.Contains(key);

                string? skipReason = null;
                if (!wasOpened)
                {
                    if (activeKeys.Contains(key))
                    {
                        skipReason = "active_position";
                    }
                    else if (tracker.CooldownKeys.Contains(key))
                    {
                        skipReason = "cooldown";
                    }
                    else if (tracker.CapitalExhaustedKeys.Contains(key))
                    {
                        skipReason = "capital_exhausted";
                    }
                    else if (tracker.MaxPositionsKeys.Contains(key))
                    {
                        skipReason = "max_positions";
                    }
                    else if (tracker.CircuitBrokenKeys.Contains(key))
                    {
                        skipReason = "exchange_circuit_broken";
                    }
                    else if (tracker.ExchangeDisabledKeys.Contains(key))
                    {
                        skipReason = "exchange_disabled";
                    }
                    else if (tracker.AssetDisabledKeys.Contains(key))
                    {
                        skipReason = "asset_disabled";
                    }
                    else if (tracker.NotSelectedKeys.Contains(key))
                    {
                        skipReason = "not_selected";
                    }
                    else
                    {
                        skipReason = "below_threshold";
                    }
                }

                return new OpportunitySnapshot
                {
                    AssetId = opp.AssetId,
                    LongExchangeId = opp.LongExchangeId,
                    ShortExchangeId = opp.ShortExchangeId,
                    SpreadPerHour = opp.SpreadPerHour,
                    NetYieldPerHour = opp.NetYieldPerHour,
                    LongVolume24h = opp.LongVolume24h,
                    ShortVolume24h = opp.ShortVolume24h,
                    WasOpened = wasOpened,
                    SkipReason = skipReason,
                    RecordedAt = DateTime.UtcNow,
                };
            }).ToList();

            await uow.OpportunitySnapshots.AddRangeAsync(snapshots, ct);

            // 7-day purge
            var cutoff = DateTime.UtcNow.AddDays(-7);
            await uow.OpportunitySnapshots.PurgeOlderThanAsync(cutoff, ct);

            await uow.SaveAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist opportunity snapshots");
        }
    }

    /// <summary>
    /// Pushes a ReceiveDashboardUpdate to the MarketData group with current KPI values.
    /// </summary>
    private async Task PushDashboardUpdateAsync(
        List<ArbitragePosition> openPositions,
        List<ArbitrageOpportunityDto> opportunities,
        bool botEnabled)
    {
        try
        {
            var totalPnl = openPositions.Sum(p => p.AccumulatedFunding);
            var bestSpread = opportunities.Count > 0
                ? opportunities.Max(o => o.SpreadPerHour)
                : 0m;

            var dto = new DashboardDto
            {
                BotEnabled = botEnabled,
                OpenPositionCount = openPositions.Count,
                TotalPnl = totalPnl,
                BestSpread = bestSpread,
            };

            await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveDashboardUpdate(dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push dashboard update via SignalR");
        }
    }

    /// <summary>
    /// Pushes a ReceivePositionUpdate for each open position to the owning user's group.
    /// </summary>
    private async Task PushPositionUpdatesAsync(List<ArbitragePosition> openPositions, BotConfiguration config)
    {
        var tasks = openPositions.Select(async pos =>
        {
            try
            {
                var dto = MapPositionToDto(pos, config);
                await _hubContext.Clients.Group($"user-{pos.UserId}").ReceivePositionUpdate(dto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push position update for #{PositionId}", pos.Id);
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Pushes ReceivePositionRemoval for reaped and closed positions to their owning user's group.
    /// </summary>
    private async Task PushPositionRemovalsAsync(
        IReadOnlyList<(int PositionId, string UserId)> reapedPositions,
        List<(int PositionId, string UserId)> closedPositions)
    {
        var tasks = reapedPositions.Concat(closedPositions).Select(async removal =>
        {
            try
            {
                await _hubContext.Clients.Group($"user-{removal.UserId}").ReceivePositionRemoval(removal.PositionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push position removal for #{PositionId}", removal.PositionId);
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Pushes a ReceiveAlert for recent unread alerts to per-user SignalR groups.
    /// </summary>
    private async Task PushNewAlertsAsync(IUnitOfWork uow)
    {
        try
        {
            var since = _lastAlertPushUtc;
            var now = DateTime.UtcNow;
            var window = now - since;

            var recentAlerts = await uow.Alerts.GetRecentUnreadAsync(window);
            _lastAlertPushUtc = now;

            // H6: Prune old IDs periodically to prevent unbounded growth
            if (_pushedAlertIds.Count > 1000)
            {
                _pushedAlertIds.Clear();
            }

            foreach (var alert in recentAlerts)
            {
                // H6: Skip already-pushed alerts to prevent duplicate pushes
                if (!_pushedAlertIds.TryAdd(alert.Id, 0))
                {
                    continue;
                }

                var dto = new AlertDto
                {
                    Id = alert.Id,
                    UserId = alert.UserId,
                    ArbitragePositionId = alert.ArbitragePositionId,
                    Type = alert.Type,
                    Severity = alert.Severity,
                    Message = alert.Message,
                    IsRead = alert.IsRead,
                    CreatedAt = alert.CreatedAt,
                };

                await _hubContext.Clients.Group($"user-{alert.UserId}").ReceiveAlert(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push alert updates via SignalR");
        }
    }

    // M-BO2: Dispose SemaphoreSlim and CTS to release kernel resources
    public override void Dispose()
    {
        if (!_disposed)
        {
            _cycleLock.Dispose();
            _immediateCts.Dispose();
            _disposed = true;
        }
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Pushes a status explanation to a specific user's SignalR group.
    /// If userId is null, broadcasts to the MarketData group (global message).
    /// </summary>
    private async Task PushStatusExplanationAsync(string? userId, string message, string severity)
    {
        try
        {
            if (userId is not null)
            {
                await _hubContext.Clients.Group($"user-{userId}").ReceiveStatusExplanation(message, severity);
            }
            else
            {
                await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveStatusExplanation(message, severity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push status explanation via SignalR");
        }
    }

    internal static PositionSummaryDto MapPositionToDto(ArbitragePosition pos, BotConfiguration config)
    {
        var dto = new PositionSummaryDto
        {
            Id = pos.Id,
            AssetSymbol = pos.Asset?.Symbol ?? "?",
            LongExchangeName = pos.LongExchange?.Name ?? "?",
            ShortExchangeName = pos.ShortExchange?.Name ?? "?",
            SizeUsdc = pos.SizeUsdc,
            MarginUsdc = pos.MarginUsdc,
            EntrySpreadPerHour = pos.EntrySpreadPerHour,
            CurrentSpreadPerHour = pos.CurrentSpreadPerHour,
            AccumulatedFunding = pos.AccumulatedFunding,
            UnrealizedPnl = pos.AccumulatedFunding, // best estimate until live mark-to-market
            RealizedPnl = pos.RealizedPnl,
            Status = pos.Status,
            OpenedAt = pos.OpenedAt,
            ClosedAt = pos.ClosedAt,
        };

        ComputeWarnings(dto, pos, config);
        return dto;
    }

    /// <summary>
    /// Evaluates warning conditions for a position and populates WarningLevel + WarningTypes.
    /// Conditions checked:
    ///   - SpreadRisk: spread approaching or below close/alert thresholds
    ///   - TimeBased:  position approaching MaxHoldTimeHours
    ///   - Loss:       unrealized loss approaching StopLossPct
    /// The highest WarningLevel across all conditions is used.
    /// </summary>
    internal static void ComputeWarnings(PositionSummaryDto dto, ArbitragePosition pos, BotConfiguration config)
    {
        var warningLevel = WarningLevel.None;
        var warningTypes = new List<WarningType>();

        // SpreadRisk warnings
        if (pos.CurrentSpreadPerHour <= config.CloseThreshold)
        {
            warningTypes.Add(WarningType.SpreadRisk);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
        }
        else if (pos.CurrentSpreadPerHour <= config.AlertThreshold)
        {
            warningTypes.Add(WarningType.SpreadRisk);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
        }

        // TimeBased warnings
        var hoursOpen = (DateTime.UtcNow - pos.OpenedAt).TotalHours;
        if (hoursOpen > config.MaxHoldTimeHours * 0.95)
        {
            warningTypes.Add(WarningType.TimeBased);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
        }
        else if (hoursOpen > config.MaxHoldTimeHours * 0.8)
        {
            warningTypes.Add(WarningType.TimeBased);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
        }

        // Loss warnings (unrealized loss relative to margin and stop-loss)
        var unrealizedLoss = -pos.AccumulatedFunding; // positive means position is losing
        var stopLossAmount = config.StopLossPct * pos.MarginUsdc;
        if (unrealizedLoss > 0 && stopLossAmount > 0)
        {
            if (unrealizedLoss > stopLossAmount * 0.9m)
            {
                warningTypes.Add(WarningType.Loss);
                warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
            }
            else if (unrealizedLoss > stopLossAmount * 0.7m)
            {
                warningTypes.Add(WarningType.Loss);
                warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
            }
        }

        // PnlProgress warnings (when adaptive hold is enabled)
        if (config.AdaptiveHoldEnabled && pos.AccumulatedFunding > 0)
        {
            var entryFee = pos.EntryFeesUsdc > 0
                ? pos.EntryFeesUsdc
                : pos.SizeUsdc * pos.Leverage * 2m * PositionHealthMonitor.GetTakerFeeRate(
                    pos.LongExchange?.Name, pos.ShortExchange?.Name,
                    pos.LongExchange?.TakerFeeRate, pos.ShortExchange?.TakerFeeRate);
            if (entryFee > 0 && config.TargetPnlMultiplier > 0)
            {
                var pnlProgress = pos.AccumulatedFunding / (config.TargetPnlMultiplier * entryFee);
                if (pnlProgress > 0.9m)
                {
                    warningTypes.Add(WarningType.PnlProgress);
                    warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
                }
                else if (pnlProgress > 0.7m)
                {
                    warningTypes.Add(WarningType.PnlProgress);
                    warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
                }
            }
        }

        dto.WarningLevel = warningLevel;
        dto.WarningTypes = warningTypes;
    }

    /// <summary>
    /// Groups skip-reason tracking sets to reduce parameter count.
    /// Per-user sets are cleared at the start of each user iteration to prevent cross-user leakage.
    /// Global sets (CapitalExhaustedKeys, MaxPositionsKeys, CooldownKeys) accumulate across all users
    /// and represent "any-user" aggregate status — User B's snapshot may report a skip reason
    /// that was only triggered by User A. This is acceptable for the current snapshot model
    /// which records one reason per opportunity per cycle (not per-user). If per-user accuracy
    /// is needed, scope these sets inside the user loop.
    /// </summary>
    private static string OpportunityKey(ArbitrageOpportunityDto o)
        => $"{o.AssetId}_{o.LongExchangeId}_{o.ShortExchangeId}";

    private static string PositionKey(ArbitragePosition p)
        => $"{p.AssetId}_{p.LongExchangeId}_{p.ShortExchangeId}";

    /// <summary>
    /// Groups skip-reason tracking sets to reduce parameter count.
    /// </summary>
    internal sealed class SkipReasonTracker
    {
        // Global sets (accumulated across all users — represents "any-user" aggregate status)
        public HashSet<string> OpenedOppKeys { get; } = new();
        public HashSet<string> CapitalExhaustedKeys { get; } = new();
        public HashSet<string> MaxPositionsKeys { get; } = new();
        public HashSet<string> CooldownKeys { get; } = new();

        // Per-user sets (cleared before each user iteration)
        public HashSet<string> ExchangeDisabledKeys { get; } = new();
        public HashSet<string> AssetDisabledKeys { get; } = new();
        public HashSet<string> CircuitBrokenKeys { get; } = new();
        public HashSet<string> NotSelectedKeys { get; } = new();

        /// <summary>Clears per-user skip reason sets before processing a new user.</summary>
        public void ClearPerUserSets()
        {
            ExchangeDisabledKeys.Clear();
            AssetDisabledKeys.Clear();
            CircuitBrokenKeys.Clear();
            NotSelectedKeys.Clear();
        }
    }
}
