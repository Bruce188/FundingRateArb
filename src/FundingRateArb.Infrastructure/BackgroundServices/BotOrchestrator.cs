using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class BotOrchestrator : BackgroundService, IBotControl, IBotDiagnostics
{
    // M2: Instance-level semaphore (not static) — avoids cross-instance interference in tests
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private bool _disposed;

    // C6: CancellationTokenSource replaces _immediateRunRequested bool — cancels the timer wait immediately
    private CancellationTokenSource _immediateCts = new();

    // M4: Extract magic polling intervals to named constants
    private const int CycleIntervalSeconds = 60;

    // Cycle counter for periodic reconciliation
    private int _cycleCount;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFundingRateReadinessSignal _readinessSignal;
    private readonly ISignalRNotifier _notifier;
    private readonly ICircuitBreakerManager _circuitBreaker;
    private readonly IOpportunityFilter _opportunityFilter;
    private readonly IRotationEvaluator _rotationEvaluator;
    private readonly ILogger<BotOrchestrator> _logger;

    public BotOrchestrator(
        IServiceScopeFactory scopeFactory,
        IFundingRateReadinessSignal readinessSignal,
        ISignalRNotifier notifier,
        ICircuitBreakerManager circuitBreaker,
        IOpportunityFilter opportunityFilter,
        IRotationEvaluator rotationEvaluator,
        ILogger<BotOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _readinessSignal = readinessSignal;
        _notifier = notifier;
        _circuitBreaker = circuitBreaker;
        _opportunityFilter = opportunityFilter;
        _rotationEvaluator = rotationEvaluator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for first FundingRateFetcher cycle to complete (with 120s timeout)
        await _readinessSignal.WaitForReadyAsync(ct);

        // Startup reconciliation: verify positions match exchange state before first cycle
        try
        {
            using var reconcileScope = _scopeFactory.CreateScope();
            var healthMonitor = reconcileScope.ServiceProvider.GetRequiredService<IPositionHealthMonitor>();
            using var reconcileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reconcileCts.CancelAfter(TimeSpan.FromMinutes(2));
            await healthMonitor.ReconcileOpenPositionsAsync(reconcileCts.Token);
            _logger.LogInformation("Startup reconciliation completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation failed — continuing with normal cycle");
        }

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
    public void ClearCooldowns() => _circuitBreaker.ClearCooldowns();
    // C6: Cancel the timer wait to trigger an immediate cycle
    public void TriggerImmediateCycle() => _immediateCts.Cancel();

    public IReadOnlyList<CircuitBreakerStatusDto> GetCircuitBreakerStates()
        => _circuitBreaker.GetCircuitBreakerStates();

    public void RecordCloseResult(decimal realizedPnl, string? userId = null)
        => _circuitBreaker.RecordCloseResult(realizedPnl, userId);

    /// <summary>
    /// One bot cycle: health-monitor ALL open positions, then iterate enabled users.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        _circuitBreaker.SweepExpiredEntries();

        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var globalConfig = await uow.BotConfig.GetActiveAsync();
        var healthMonitor = scope.ServiceProvider.GetRequiredService<IPositionHealthMonitor>();
        var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
        var executionEngine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        var userSettings = scope.ServiceProvider.GetRequiredService<IUserSettingsService>();
        var ctx = new CycleContext(uow, globalConfig, healthMonitor, signalEngine, executionEngine, userSettings);

        // Step 1: Always run health monitor for ALL open positions (regardless of user or bot state)
        var healthResult = await healthMonitor.CheckAndActAsync(ct);

        var closedPositionIds = new List<(int PositionId, string UserId)>();
        foreach (var (pos, reason) in healthResult.ToClose)
        {
            await executionEngine.ClosePositionAsync(pos.UserId, pos, reason, ct);
            if (pos.Status == PositionStatus.Closed && pos.RealizedPnl.HasValue && !pos.IsDryRun)
            {
                RecordCloseResult(pos.RealizedPnl.Value, pos.UserId);
                closedPositionIds.Add((pos.Id, pos.UserId));

                // Apply cooldown for losing trades to avoid re-entering the same pair
                if (pos.RealizedPnl.Value < 0)
                {
                    var opKey = $"{pos.UserId}:{pos.AssetId}:{pos.LongExchangeId}:{pos.ShortExchangeId}";
                    _circuitBreaker.SetCooldown(opKey, DateTime.UtcNow.Add(_circuitBreaker.BaseCooldownDuration), 1);
                }
            }
        }

        // Periodic exchange reconciliation: every N cycles, verify Open positions still exist on exchanges
        // Runs after the close loop so time-sensitive closes execute first
        if (globalConfig.ReconciliationIntervalCycles > 0 && ++_cycleCount >= globalConfig.ReconciliationIntervalCycles)
        {
            _cycleCount = 0;
            try
            {
                await healthMonitor.ReconcileOpenPositionsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Periodic reconciliation failed — will retry next interval");
            }
        }

        // Push removal events for reaped and closed positions
        await _notifier.PushPositionRemovalsAsync(healthResult.ReapedPositions, closedPositionIds);

        // Trigger circuit breaker for exchanges involved in reaped positions
        // Only count Closing-reaped as exchange failures — Opening-reaped are asset/tx failures, not exchange issues
        foreach (var reaped in healthResult.ReapedPositions)
        {
            if (reaped.OriginalStatus == PositionStatus.Opening)
            {
                continue;
            }

            _circuitBreaker.IncrementExchangeFailure(reaped.LongExchangeId, globalConfig);
            if (reaped.ShortExchangeId != reaped.LongExchangeId)
            {
                _circuitBreaker.IncrementExchangeFailure(reaped.ShortExchangeId, globalConfig);
            }
        }

        // Emergency close exchange cleanup for reaped Closing positions
        // Skip Opening-reaped — may never have had legs placed on exchanges
        foreach (var reaped in healthResult.ReapedPositions.Where(r => r.OriginalStatus != PositionStatus.Opening))
        {
            try
            {
                using var closeScope = _scopeFactory.CreateScope();
                var closeEngine = closeScope.ServiceProvider.GetRequiredService<IExecutionEngine>();
                var closeUow = closeScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var pos = await closeUow.Positions.GetByIdAsync(reaped.PositionId);
                if (pos is null)
                {
                    continue;
                }

                using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                closeCts.CancelAfter(TimeSpan.FromSeconds(30));
                await closeEngine.ClosePositionAsync(pos.UserId, pos, CloseReason.ExchangeDrift, closeCts.Token);
                _logger.LogWarning("Emergency close attempted for reaped position #{Id}", reaped.PositionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Emergency close failed for reaped position #{Id}", reaped.PositionId);
            }
        }

        // Fetch ALL open positions after health monitor so closed positions are excluded
        var allOpenPositions = await uow.Positions.GetOpenAsync();

        // Hoist Opening positions query once per cycle (shared across all users)
        var allOpeningPositions = await uow.Positions.GetByStatusAsync(PositionStatus.Opening);

        // Push per-user position updates with warning computation and computed PnL (always, regardless of bot state)
        await _notifier.PushPositionUpdatesAsync(allOpenPositions, globalConfig, healthResult.ComputedPnl);
        await _notifier.PushNewAlertsAsync(uow);

        // Step 2: Compute + push opportunities globally (always, regardless of bot state)
        var opportunityResult = await signalEngine.GetOpportunitiesWithDiagnosticsAsync(ct);
        var allOpportunities = opportunityResult.Opportunities;

        // Populate circuit breaker statuses for dashboard display
        var cbStates = _circuitBreaker.GetCircuitBreakerStates();
        if (cbStates.Count > 0)
        {
            // Build exchange ID → name lookup from opportunity data
            var exchangeNameLookup = new Dictionary<int, string>();
            foreach (var opp in allOpportunities)
            {
                exchangeNameLookup.TryAdd(opp.LongExchangeId, opp.LongExchangeName);
                exchangeNameLookup.TryAdd(opp.ShortExchangeId, opp.ShortExchangeName);
            }

            // Fall back to DB if an exchange isn't in the opportunity list
            if (cbStates.Any(cb => !exchangeNameLookup.ContainsKey(cb.ExchangeId)))
            {
                var exchanges = await uow.Exchanges.GetAllAsync();
                foreach (var ex in exchanges)
                {
                    exchangeNameLookup.TryAdd(ex.Id, ex.Name);
                }
            }

            // Enrich exchange names from lookup (GetCircuitBreakerStates returns generic names)
            opportunityResult.CircuitBreakers = cbStates
                .Select(cb => new CircuitBreakerStatusDto
                {
                    ExchangeId = cb.ExchangeId,
                    ExchangeName = exchangeNameLookup.GetValueOrDefault(cb.ExchangeId, $"Exchange #{cb.ExchangeId}"),
                    BrokenUntil = cb.BrokenUntil,
                    RemainingMinutes = cb.RemainingMinutes
                })
                .ToList();
        }

        await _notifier.PushOpportunityUpdateAsync(opportunityResult);

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
                    await _notifier.PushRebalanceRemovalsAsync(rebalancedRemovals);

                    // Filter only actually closed positions so unclosed ones still get health monitoring
                    allOpenPositions = allOpenPositions.Where(p => !closedIds.Contains(p.Id)).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Portfolio rebalancing failed — continuing with normal cycle");
            }
        }

        // Step 3: Push global dashboard KPI update (includes opening and needs-attention counts)
        var needsAttentionCount = await uow.Positions.CountByStatusesAsync(PositionStatus.EmergencyClosed, PositionStatus.Failed);
        await _notifier.PushDashboardUpdateAsync(allOpenPositions, allOpportunities, globalConfig.IsEnabled,
            allOpeningPositions.Count, needsAttentionCount);

        // Step 4: Gate — skip position opening if global kill switch is off
        if (!globalConfig.IsEnabled)
        {
            _logger.LogDebug("Bot is disabled (kill switch). Skipping cycle.");
            await _notifier.PushStatusExplanationAsync(null, "Bot is disabled — enable in Settings or Admin > Bot Config", "danger");
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

        // Derive circuit-broken exchange IDs from the already-fetched cbStates (avoids redundant dictionary scan)
        var circuitBrokenExchangeIds = cbStates.Select(cb => cb.ExchangeId).ToHashSet();

        // Track known opportunity keys for adaptive candidate dedup (value-based, no reference equality)
        var knownOpportunityKeys = new HashSet<string>(allOpportunities.Select(o => o.OpportunityKey()));

        // Create a local snapshot list for adaptive appends so we don't mutate the signal engine's DTO
        var snapshotOpportunities = new List<ArbitrageOpportunityDto>(allOpportunities);

        foreach (var userId in enabledUserIds)
        {
            // Clear per-user skip reason sets to prevent cross-user leakage
            tracker.ClearPerUserSets();
            try
            {
                await ExecuteUserCycleAsync(userId, ctx, opportunityResult, allOpenPositions, allOpeningPositions, dataOnlyExchangeIds, circuitBrokenExchangeIds, knownOpportunityKeys, snapshotOpportunities, tracker, ct);
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
        CycleContext ctx,
        OpportunityResultDto opportunityResult,
        List<ArbitragePosition> allOpenPositions,
        List<ArbitragePosition> allOpeningPositions,
        HashSet<int> dataOnlyExchangeIds,
        HashSet<int> circuitBrokenExchangeIds,
        HashSet<string> knownOpportunityKeys,
        List<ArbitrageOpportunityDto> snapshotOpportunities,
        SkipReasonTracker tracker,
        CancellationToken ct)
    {
        var allOpportunities = opportunityResult.Opportunities;
        var userConfig = await ctx.UserSettings.GetOrCreateConfigAsync(userId);
        if (!userConfig.IsEnabled)
        {
            return;
        }

        // Check user has valid credentials (at least 2 exchanges)
        if (!await ctx.UserSettings.HasValidCredentialsAsync(userId))
        {
            await _notifier.PushStatusExplanationAsync(userId, "No exchange API keys configured — set up in Settings > API Keys", "danger");
            return;
        }

        // Get user's enabled exchange and asset IDs
        var enabledExchangeIds = await ctx.UserSettings.GetUserEnabledExchangeIdsAsync(userId);
        var enabledAssetIds = await ctx.UserSettings.GetUserEnabledAssetIdsAsync(userId);

        var enabledExchangeSet = enabledExchangeIds.ToHashSet();
        var enabledAssetSet = enabledAssetIds.ToHashSet();

        // Filter global opportunities to user's preferences, excluding data-only and circuit-broken exchanges
        var userOpportunities = _opportunityFilter.FilterUserOpportunities(
            allOpportunities, enabledExchangeSet, dataOnlyExchangeIds, circuitBrokenExchangeIds, enabledAssetSet, userConfig, tracker);

        // Check user's open positions vs their limit
        var userOpenPositions = allOpenPositions.Where(p => p.UserId == userId).ToList();

        // Daily drawdown circuit breaker for this user
        var closedToday = await ctx.Uow.Positions.GetClosedSinceAsync(DateTime.UtcNow.Date);
        var userClosedToday = closedToday.Where(p => p.UserId == userId).ToList();
        var dailyPnl = userClosedToday.Where(p => !p.IsDryRun).Sum(p => p.RealizedPnl ?? 0m)
                     + userOpenPositions.Where(p => !p.IsDryRun).Sum(p => p.AccumulatedFunding);
        var drawdownLimit = userConfig.TotalCapitalUsdc * userConfig.DailyDrawdownPausePct;
        if (dailyPnl < -drawdownLimit)
        {
            _logger.LogWarning(
                "Daily drawdown limit hit for user {UserId}: {DailyPnl:F2} USDC (limit: -{Limit:F2})",
                userId, dailyPnl, drawdownLimit);
            await _notifier.PushStatusExplanationAsync(userId,
                $"Daily drawdown limit hit ({dailyPnl:F2} USDC, limit: -{drawdownLimit:F2}) — pausing position opens", "warning");
            return;
        }

        // Consecutive loss circuit breaker for this user
        var consecutiveLosses = _circuitBreaker.GetConsecutiveLosses(userId);
        if (consecutiveLosses >= userConfig.ConsecutiveLossPause)
        {
            _logger.LogWarning(
                "Consecutive loss limit reached for user {UserId} ({Count}/{Max})",
                userId, consecutiveLosses, userConfig.ConsecutiveLossPause);
            await _notifier.PushStatusExplanationAsync(userId,
                $"Consecutive loss limit reached ({consecutiveLosses}/{userConfig.ConsecutiveLossPause}) — pausing position opens", "warning");
            return;
        }

        // Filter hoisted Opening positions for this user (no per-user DB query)
        var userOpeningPositions = allOpeningPositions.Where(p => p.UserId == userId).ToList();

        // Max positions gate (includes Opening positions to prevent rapid-fire loop)
        if ((userOpenPositions.Count + userOpeningPositions.Count) >= userConfig.MaxConcurrentPositions)
        {
            // Evaluate position rotation when all slots are full
            if (userOpenPositions.Count > 0 && userOpportunities.Count > 0)
            {
                var rotationRec = _rotationEvaluator.Evaluate(
                    userOpenPositions, userOpportunities, userConfig, ctx.GlobalConfig);

                if (rotationRec is not null)
                {
                    // Check daily rotation cap
                    var today = DateOnly.FromDateTime(DateTime.UtcNow);
                    var (date, count) = _circuitBreaker.GetDailyRotationCount(userId);
                    if (date != today)
                    {
                        count = 0; // Reset counter on new day
                    }

                    var rotationExecuted = false;

                    if (count >= userConfig.MaxRotationsPerDay)
                    {
                        _logger.LogDebug("Rotation skipped for {UserId} — daily cap reached ({Count}/{Max})",
                            userId, count, userConfig.MaxRotationsPerDay);
                    }
                    else
                    {
                        // Check rotation cooldown for the replacement opportunity
                        var cooldownKey = $"{userId}:{rotationRec.ReplacementAssetId}:{rotationRec.ReplacementLongExchangeId}:{rotationRec.ReplacementShortExchangeId}";
                        var rotationCooldownUntil = _circuitBreaker.GetRotationCooldown(cooldownKey);
                        if (rotationCooldownUntil.HasValue && DateTime.UtcNow < rotationCooldownUntil.Value)
                        {
                            _logger.LogDebug("Rotation skipped — replacement opportunity on cooldown until {Until}", rotationCooldownUntil.Value);
                        }
                        else
                        {
                            // Execute rotation: close worst position
                            var positionToClose = userOpenPositions.FirstOrDefault(p => p.Id == rotationRec.PositionId);
                            if (positionToClose is null)
                            {
                                _logger.LogWarning("Rotation target position {PositionId} not found in user open positions", rotationRec.PositionId);
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Rotating position {PositionId} ({Asset} spread={Spread:F6}/hr) → {Replacement} (yield={Yield:F6}/hr, improvement={Improvement:F6}/hr)",
                                    rotationRec.PositionId, rotationRec.PositionAsset, rotationRec.CurrentSpreadPerHour,
                                    rotationRec.ReplacementAsset, rotationRec.ReplacementNetYieldPerHour, rotationRec.ImprovementPerHour);

                                try
                                {
                                    await ctx.ExecutionEngine.ClosePositionAsync(userId, positionToClose, CloseReason.Rotation, ct);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Rotation close threw for position {PositionId}", positionToClose.Id);
                                }

                                // Always set cooldown to prevent retry storms, even on failure/exception
                                _circuitBreaker.SetRotationCooldown(cooldownKey, DateTime.UtcNow.Add(_circuitBreaker.RotationCooldownDuration));

                                // Only count as success if position fully closed
                                if (positionToClose.Status == PositionStatus.Closed)
                                {
                                    _circuitBreaker.SetDailyRotationCount(userId, today, count + 1);
                                    userOpenPositions = userOpenPositions.Where(p => p.Id != rotationRec.PositionId).ToList();
                                    rotationExecuted = true;
                                }
                                else
                                {
                                    _logger.LogWarning("Rotation close did not complete for position {PositionId} — status is {Status}", positionToClose.Id, positionToClose.Status);
                                }
                            }
                        }
                    }

                    if (rotationExecuted)
                    {
                        // Rotation freed a slot — continue to normal candidate filtering + opening
                        // The freed slot will be picked up by the existing open-position logic
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Max concurrent positions reached for user {UserId} ({Open}+{Opening}/{Max})",
                            userId, userOpenPositions.Count, userOpeningPositions.Count, userConfig.MaxConcurrentPositions);
                        await _notifier.PushStatusExplanationAsync(userId,
                            $"{userOpenPositions.Count + userOpeningPositions.Count}/{userConfig.MaxConcurrentPositions} position slots occupied", "info");
                        return;
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Max concurrent positions reached for user {UserId} ({Open}+{Opening}/{Max})",
                        userId, userOpenPositions.Count, userOpeningPositions.Count, userConfig.MaxConcurrentPositions);
                    await _notifier.PushStatusExplanationAsync(userId,
                        $"{userOpenPositions.Count + userOpeningPositions.Count}/{userConfig.MaxConcurrentPositions} position slots occupied", "info");
                    return;
                }
            }
            else
            {
                _logger.LogDebug(
                    "Max concurrent positions reached for user {UserId} ({Open}+{Opening}/{Max})",
                    userId, userOpenPositions.Count, userOpeningPositions.Count, userConfig.MaxConcurrentPositions);
                await _notifier.PushStatusExplanationAsync(userId,
                    $"{userOpenPositions.Count + userOpeningPositions.Count}/{userConfig.MaxConcurrentPositions} position slots occupied", "info");
                return;
            }
        }

        // Filter candidates: exclude active/opening positions and cooled-down opportunities
        var allActiveKeys = userOpenPositions
            .Concat(userOpeningPositions)
            .Select(p => p.PositionKey())
            .ToHashSet();

        var filteredCandidates = _opportunityFilter.FilterCandidates(
            userOpportunities, allActiveKeys, userId, tracker, out var cooldownSkips);

        var takeCount = userConfig.AllocationStrategy == AllocationStrategy.Concentrated ? 1 : userConfig.AllocationTopN;
        var candidates = filteredCandidates.Take(takeCount).ToList();

        // Track opportunities that passed all filters but weren't selected by allocation strategy
        foreach (var opp in filteredCandidates.Skip(takeCount))
        {
            tracker.NotSelectedKeys.Add(opp.OpportunityKey());
        }

        if (candidates.Count == 0)
        {
            // Adaptive threshold fallback: when user has 0 open positions, try net-positive opportunities below threshold
            if (userOpenPositions.Count == 0 && opportunityResult.AllNetPositive.Count > 0)
            {
                var adaptiveCandidates = _opportunityFilter.FindAdaptiveCandidates(
                    opportunityResult.AllNetPositive, enabledExchangeSet, dataOnlyExchangeIds,
                    circuitBrokenExchangeIds, enabledAssetSet, allActiveKeys, userId, tracker);

                if (adaptiveCandidates.Count > 0)
                {
                    var bestAdaptive = adaptiveCandidates[0];
                    _logger.LogInformation(
                        "Adaptive threshold for user {UserId}: {Asset} at {Net:F6}/hr (below normal threshold {Threshold:F6})",
                        userId, bestAdaptive.AssetSymbol, bestAdaptive.NetYieldPerHour, userConfig.OpenThreshold);
                    await _notifier.PushStatusExplanationAsync(userId,
                        $"Adaptive threshold: opening {bestAdaptive.AssetSymbol} at {bestAdaptive.NetYieldPerHour * 100:F4}%/hr (below normal threshold)", "info");

                    // Use the normal execution path with the adaptive candidate
                    candidates = adaptiveCandidates;

                    // B1: Ensure adaptive candidate is tracked in snapshotOpportunities (not the signal engine's DTO)
                    // so PersistOpportunitySnapshotsAsync can record WasOpened = true instead of "below_threshold"
                    foreach (var ac in adaptiveCandidates)
                    {
                        if (knownOpportunityKeys.Add(ac.OpportunityKey()))
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
                    await _notifier.PushStatusExplanationAsync(userId, "No arbitrage opportunities detected this cycle", "info");
                }
                else if (userOpportunities.Count == 0)
                {
                    if (tracker.CircuitBrokenKeys.Count > 0)
                    {
                        // Collect unique exchange names from circuit-broken opportunities
                        var cbExchangeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var o in allOpportunities)
                        {
                            var oKey = o.OpportunityKey();
                            if (!tracker.CircuitBrokenKeys.Contains(oKey))
                            {
                                continue;
                            }

                            if (circuitBrokenExchangeIds.Contains(o.LongExchangeId))
                            {
                                cbExchangeNames.Add(o.LongExchangeName);
                            }

                            if (circuitBrokenExchangeIds.Contains(o.ShortExchangeId))
                            {
                                cbExchangeNames.Add(o.ShortExchangeName);
                            }
                        }

                        if (cbExchangeNames.Count > 0)
                        {
                            // Reuse already-populated CircuitBreakers list for name+time lookup
                            var parts = new List<string>();
                            foreach (var name in cbExchangeNames)
                            {
                                var cbDto = opportunityResult.CircuitBreakers
                                    .FirstOrDefault(cb => cb.ExchangeName.Equals(name, StringComparison.OrdinalIgnoreCase));

                                if (cbDto != null && cbDto.RemainingMinutes > 0)
                                {
                                    parts.Add($"{name} circuit breaker active (resumes in {cbDto.RemainingMinutes}m)");
                                }
                                else
                                {
                                    parts.Add($"{name} circuit breaker active");
                                }
                            }

                            await _notifier.PushStatusExplanationAsync(userId, string.Join("; ", parts), "warning");
                        }
                        else
                        {
                            await _notifier.PushStatusExplanationAsync(userId, "No opportunities match your enabled exchanges and coins", "info");
                        }
                    }
                    else if (tracker.BelowThresholdCount > 0)
                    {
                        await _notifier.PushStatusExplanationAsync(userId,
                            $"{tracker.BelowThresholdCount} opportunities below your threshold (best: {tracker.BestBelowThresholdYield * 100:F4}%/hr, yours: {userConfig.OpenThreshold * 100:F4}%/hr)", "info");
                    }
                    else if (tracker.ExchangeDisabledKeys.Count > 0)
                    {
                        await _notifier.PushStatusExplanationAsync(userId, "No opportunities match your enabled exchanges", "info");
                    }
                    else if (tracker.AssetDisabledKeys.Count > 0)
                    {
                        await _notifier.PushStatusExplanationAsync(userId, "No opportunities match your enabled coins", "info");
                    }
                    else
                    {
                        await _notifier.PushStatusExplanationAsync(userId, "No arbitrage opportunities detected this cycle", "info");
                    }
                }
                else if (cooldownSkips.Count > 0)
                {
                    var first = cooldownSkips[0];
                    var minutes = (int)Math.Ceiling(first.Remaining.TotalMinutes);
                    await _notifier.PushStatusExplanationAsync(userId,
                        $"{first.Asset} cooling down — retry in {minutes} minutes", "info");
                }
                else
                {
                    var bestOpp = userOpportunities.OrderByDescending(o => o.SpreadPerHour).First();
                    await _notifier.PushStatusExplanationAsync(userId,
                        $"Best spread {(bestOpp.SpreadPerHour * 100):F2}%/hr — all opportunities have active positions", "info");
                }
                return;
            }
        }

        using var sizerScope = _scopeFactory.CreateScope();
        var positionSizer = sizerScope.ServiceProvider.GetRequiredService<IPositionSizer>();
        var balanceAggregator = sizerScope.ServiceProvider.GetRequiredService<IBalanceAggregator>();
        var sizes = await positionSizer.CalculateBatchSizesAsync(candidates, userConfig.AllocationStrategy, userId, userConfig, ct);

        // Push balance snapshot to user's dashboard
        try
        {
            var balanceSnapshot = await balanceAggregator.GetBalanceSnapshotAsync(userId, ct);
            await _notifier.PushBalanceUpdateAsync(userId, balanceSnapshot);
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
                tracker.CapitalExhaustedKeys.Add(opp.OpportunityKey());
                continue;
            }

            if (slotsAvailable <= 0)
            {
                // Track remaining candidates as skipped due to max positions
                for (int remaining = idx; remaining < candidates.Count; remaining++)
                {
                    var remOpp = candidates[remaining];
                    tracker.MaxPositionsKeys.Add(remOpp.OpportunityKey());
                }
                break;
            }

            var key = opp.OpportunityKey();
            var cooldownKey = $"{userId}:{key}";

            _logger.LogInformation(
                "Opening position for user {UserId}: {Asset} {LongExchange}/{ShortExchange} size={Size} USDC",
                userId, opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, size);

            bool success;
            string? error;
            try
            {
                (success, error) = await ctx.ExecutionEngine.OpenPositionAsync(userId, opp, size, userConfig, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenPositionAsync threw for {Asset} on {LongExchange}/{ShortExchange}",
                    opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName);
                success = false;
                error = null; // Force generic failure path — avoids misrouting on "balance" in ex.Message
            }

            if (success)
            {
                slotsAvailable--;
                _circuitBreaker.RemoveCooldown(cooldownKey);
                tracker.OpenedOppKeys.Add(key);
                positionsChanged = true;

                // Reset circuit breaker on success.
                _circuitBreaker.RemoveExchangeCircuitBreaker(opp.LongExchangeId);
                _circuitBreaker.RemoveExchangeCircuitBreaker(opp.ShortExchangeId);

                // Reset asset-exchange cooldowns on successful open
                _circuitBreaker.RemoveAssetExchangeCooldown(opp.AssetId, opp.LongExchangeId);
                _circuitBreaker.RemoveAssetExchangeCooldown(opp.AssetId, opp.ShortExchangeId);

                var msg = $"Opened position: {opp.AssetSymbol} {opp.LongExchangeName}/{opp.ShortExchangeName}";
                await _notifier.PushNotificationAsync(userId, msg);
                await _notifier.PushNewAlertsAsync(ctx.Uow);
            }
            else if (error != null && (error.Contains("Insufficient margin", StringComparison.OrdinalIgnoreCase)
                                       || error.Contains("balance", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Balance exhausted for user {UserId}: {Error}", userId, error);
                await _notifier.PushStatusExplanationAsync(userId,
                    $"Insufficient balance on {opp.LongExchangeName}/{opp.ShortExchangeName} for {opp.AssetSymbol} (need {size:F2} USDC per exchange)", "warning");
                // Track remaining candidates as skipped due to capital exhaustion
                for (int remaining = idx + 1; remaining < candidates.Count; remaining++)
                {
                    var remOpp = candidates[remaining];
                    tracker.CapitalExhaustedKeys.Add(remOpp.OpportunityKey());
                }
                // Circuit-break only the exchange that reported the margin error (not both)
                var brokenUntil = DateTime.UtcNow.AddMinutes(ctx.GlobalConfig.ExchangeCircuitBreakerMinutes);
                var marginErrorExchange = ExtractMarginErrorExchange(error);
                if (marginErrorExchange != null)
                {
                    // Targeted: only break the specific exchange that reported insufficient margin
                    if (marginErrorExchange.Equals(opp.LongExchangeName, StringComparison.OrdinalIgnoreCase))
                    {
                        _circuitBreaker.SetExchangeCircuitBreaker(opp.LongExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerThreshold, brokenUntil);
                        _logger.LogWarning(
                            "Circuit breaker OPEN for exchange {ExchangeId} ({Name}) due to margin error — excluded for {Minutes}m",
                            opp.LongExchangeId, opp.LongExchangeName, ctx.GlobalConfig.ExchangeCircuitBreakerMinutes);
                        circuitBrokenExchangeIds.Add(opp.LongExchangeId);
                    }
                    else if (marginErrorExchange.Equals(opp.ShortExchangeName, StringComparison.OrdinalIgnoreCase))
                    {
                        _circuitBreaker.SetExchangeCircuitBreaker(opp.ShortExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerThreshold, brokenUntil);
                        _logger.LogWarning(
                            "Circuit breaker OPEN for exchange {ExchangeId} ({Name}) due to margin error — excluded for {Minutes}m",
                            opp.ShortExchangeId, opp.ShortExchangeName, ctx.GlobalConfig.ExchangeCircuitBreakerMinutes);
                        circuitBrokenExchangeIds.Add(opp.ShortExchangeId);
                    }
                    else
                    {
                        // Exchange name in error doesn't match either leg — break both as safe fallback
                        _circuitBreaker.SetExchangeCircuitBreaker(opp.LongExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerThreshold, brokenUntil);
                        circuitBrokenExchangeIds.Add(opp.LongExchangeId);
                        if (opp.ShortExchangeId != opp.LongExchangeId)
                        {
                            _circuitBreaker.SetExchangeCircuitBreaker(opp.ShortExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerThreshold, brokenUntil);
                            circuitBrokenExchangeIds.Add(opp.ShortExchangeId);
                        }
                        _logger.LogWarning(
                            "Circuit breaker OPEN for BOTH exchanges {LongId}/{ShortId} — margin error exchange '{ErrorExchange}' not matched",
                            opp.LongExchangeId, opp.ShortExchangeId, marginErrorExchange);
                    }
                }
                else
                {
                    // Generic error (no specific exchange identified) — break both as safe fallback
                    _circuitBreaker.SetExchangeCircuitBreaker(opp.LongExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerThreshold, brokenUntil);
                    _logger.LogWarning(
                        "Circuit breaker OPEN for exchange {ExchangeId} due to margin error — excluded for {Minutes}m",
                        opp.LongExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerMinutes);
                    circuitBrokenExchangeIds.Add(opp.LongExchangeId);
                    if (opp.ShortExchangeId != opp.LongExchangeId)
                    {
                        _circuitBreaker.SetExchangeCircuitBreaker(opp.ShortExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerThreshold, brokenUntil);
                        _logger.LogWarning(
                            "Circuit breaker OPEN for exchange {ExchangeId} due to margin error — excluded for {Minutes}m",
                            opp.ShortExchangeId, ctx.GlobalConfig.ExchangeCircuitBreakerMinutes);
                        circuitBrokenExchangeIds.Add(opp.ShortExchangeId);
                    }
                }
                break;
            }
            else
            {
                var existingEntry = _circuitBreaker.GetCooldownEntry(cooldownKey);
                var failures = existingEntry.Failures + 1;
                var delay = TimeSpan.FromTicks(
                    Math.Min(_circuitBreaker.BaseCooldownDuration.Ticks * (1L << Math.Min(failures - 1, 4)), _circuitBreaker.MaxCooldownDuration.Ticks));
                _circuitBreaker.SetCooldown(cooldownKey, DateTime.UtcNow + delay, failures);

                // Identify the culpable exchange from error context (used for both cooldown and circuit breaker)
                var failingExchangeId = ExtractFailingExchange(error, opp);

                // Track asset-exchange level failures — target only the culpable exchange
                if (failingExchangeId.HasValue)
                {
                    _circuitBreaker.IncrementAssetExchangeFailure(opp.AssetId, failingExchangeId.Value);
                }
                else
                {
                    // Fallback: increment both when the failing exchange can't be identified
                    _circuitBreaker.IncrementAssetExchangeFailure(opp.AssetId, opp.LongExchangeId);
                    if (opp.ShortExchangeId != opp.LongExchangeId)
                    {
                        _circuitBreaker.IncrementAssetExchangeFailure(opp.AssetId, opp.ShortExchangeId);
                    }
                }

                // Target circuit breaker to the failing exchange
                if (failingExchangeId.HasValue)
                {
                    _circuitBreaker.IncrementExchangeFailure(failingExchangeId.Value, ctx.GlobalConfig);
                }
                else
                {
                    _circuitBreaker.IncrementExchangeFailure(opp.LongExchangeId, ctx.GlobalConfig);
                    if (opp.ShortExchangeId != opp.LongExchangeId)
                    {
                        _circuitBreaker.IncrementExchangeFailure(opp.ShortExchangeId, ctx.GlobalConfig);
                    }
                }

                _logger.LogWarning(
                    "Opportunity {Asset} {Long}/{Short} failed for user {UserId} ({Failures} consecutive). Cooldown until {Until}",
                    opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, userId, failures, DateTime.UtcNow + delay);
                _logger.LogError("Failed to open position: {Error}", error);
                await _notifier.PushNewAlertsAsync(ctx.Uow);
            }
        }

        // NB4: Refresh open positions once after the entire user loop, not per-open
        if (positionsChanged)
        {
            var updatedPositions = await ctx.Uow.Positions.GetOpenAsync();
            allOpenPositions.Clear();
            allOpenPositions.AddRange(updatedPositions);

            var updatedOpening = await ctx.Uow.Positions.GetByStatusAsync(PositionStatus.Opening);
            allOpeningPositions.Clear();
            allOpeningPositions.AddRange(updatedOpening);

            await _notifier.PushPositionUpdatesAsync(updatedPositions, ctx.GlobalConfig);
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
                .Select(p => p.PositionKey())
                .ToHashSet();

            var snapshots = opportunities.Select(opp =>
            {
                var key = opp.OpportunityKey();
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
    /// Extracts the exchange name from a margin error message.
    /// Expected format: "Insufficient margin on {ExchangeName}: available=X, required=Y"
    /// Returns null if the pattern is not found.
    /// </summary>
    internal static string? ExtractMarginErrorExchange(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        const string prefix = "Insufficient margin on ";
        var idx = error.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + prefix.Length;
        var colonIdx = error.IndexOf(':', start);
        if (colonIdx < 0)
        {
            return null;
        }

        return error[start..colonIdx].Trim();
    }

    /// <summary>
    /// Extracts the failing exchange ID from a non-margin error message by checking
    /// if the error contains either exchange name. Returns null (fall back to both)
    /// if neither name is found.
    /// </summary>
    internal static int? ExtractFailingExchange(string? error, ArbitrageOpportunityDto opp)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        var containsLong = error.Contains(opp.LongExchangeName, StringComparison.OrdinalIgnoreCase);
        var containsShort = error.Contains(opp.ShortExchangeName, StringComparison.OrdinalIgnoreCase);

        if (containsLong && !containsShort)
        {
            return opp.LongExchangeId;
        }

        if (containsShort && !containsLong)
        {
            return opp.ShortExchangeId;
        }

        // Both or neither found — can't determine, fall back to null (both incremented)
        return null;
    }

}
