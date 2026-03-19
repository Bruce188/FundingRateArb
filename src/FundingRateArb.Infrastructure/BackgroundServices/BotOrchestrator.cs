using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class BotOrchestrator : BackgroundService
{
    // M2: Instance-level semaphore (not static) — avoids cross-instance interference in tests
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private bool _disposed;

    // M4: Extract magic polling intervals to named constants
    private const int CycleIntervalSeconds = 60;
    // M8: 65s gives FundingRateFetcher one full 60s cycle + 5s margin to complete its first fetch
    private const int StartupDelaySeconds = 65;

    // M7: Track when alerts were last pushed so each cycle uses only the elapsed window (no replays)
    private DateTime _lastAlertPushUtc = DateTime.UtcNow;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hubContext;
    private readonly ILogger<BotOrchestrator> _logger;

    public BotOrchestrator(
        IServiceScopeFactory scopeFactory,
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        ILogger<BotOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext   = hubContext;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Wait for first FundingRateFetcher cycle to complete
        await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), ct);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CycleIntervalSeconds));

        while (await timer.WaitForNextTickAsync(ct))
        {
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

    /// <summary>
    /// One bot cycle: health-monitor open positions → find & open one new opportunity.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow         = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var config      = await uow.BotConfig.GetActiveAsync();

        var healthMonitor   = scope.ServiceProvider.GetRequiredService<IPositionHealthMonitor>();
        var signalEngine    = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
        var positionSizer   = scope.ServiceProvider.GetRequiredService<IPositionSizer>();
        var executionEngine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();

        // Step 1: Monitor and auto-close stale/unhealthy positions (only when enabled)
        if (config.IsEnabled)
            await healthMonitor.CheckAndActAsync(ct);

        // M6: Fetch open positions AFTER health monitor so closed positions are excluded
        var openPositions = await uow.Positions.GetOpenAsync();

        // Push position updates + alerts (always, regardless of bot state)
        await PushPositionUpdatesAsync(openPositions);
        await PushNewAlertsAsync(uow);

        // Step 2: Compute + push opportunities (always, regardless of bot state)
        var opportunities = await signalEngine.GetOpportunitiesAsync(ct);

        // H8: Push opportunity updates here (moved from FundingRateFetcher to keep SRP)
        try
        {
            await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveOpportunityUpdate(opportunities);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push opportunity update via SignalR");
        }

        // Step 3: Push dashboard KPI update (always, regardless of bot state)
        await PushDashboardUpdateAsync(openPositions, config.IsEnabled);

        // Step 4: Gate — skip position opening if bot is disabled
        if (!config.IsEnabled)
        {
            _logger.LogDebug("Bot is disabled (kill switch). Skipping cycle.");
            return;
        }

        // Step 5: Gate — skip if max positions reached
        if (openPositions.Count >= config.MaxConcurrentPositions)
        {
            _logger.LogDebug(
                "Max concurrent positions reached ({Count}/{Max}). No new positions this cycle.",
                openPositions.Count, config.MaxConcurrentPositions);
            return;
        }

        // Step 6: Find new opportunities — skip pairs already open
        var openKeys = openPositions
            .Select(p => $"{p.AssetId}_{p.LongExchangeId}_{p.ShortExchangeId}")
            .ToHashSet();

        foreach (var opp in opportunities)
        {
            var key = $"{opp.AssetId}_{opp.LongExchangeId}_{opp.ShortExchangeId}";
            if (openKeys.Contains(key)) continue;

            var size = await positionSizer.CalculateOptimalSizeAsync(opp);
            if (size <= 0) continue;

            _logger.LogInformation(
                "Opening position: {Asset} {LongExchange}/{ShortExchange} size={Size} USDC",
                opp.AssetSymbol, opp.LongExchangeName, opp.ShortExchangeName, size);

            var (success, error) = await executionEngine.OpenPositionAsync(opp, size, ct);

            if (success)
            {
                // H7: Route to owning user + admins only (Clients.All would reveal trading to all users)
                var msg = $"Opened position: {opp.AssetSymbol} {opp.LongExchangeName}/{opp.ShortExchangeName}";
                await _hubContext.Clients.Group($"user-{config.UpdatedByUserId}").ReceiveNotification(msg);
                await _hubContext.Clients.Group(HubGroups.Admins).ReceiveNotification(msg);

                // H3: Re-fetch after opening to include new position
                openPositions = await uow.Positions.GetOpenAsync();
                await PushPositionUpdatesAsync(openPositions);
                await PushNewAlertsAsync(uow);
            }
            else
            {
                _logger.LogError("Failed to open position: {Error}", error);
                // Push alerts for emergency close failures
                await PushNewAlertsAsync(uow);
            }

            break; // One position per cycle for safety
        }
    }

    /// <summary>
    /// Pushes a ReceiveDashboardUpdate to the MarketData group with current KPI values.
    /// H3: Accepts pre-fetched positions to avoid redundant GetOpenAsync calls.
    /// H-BO1: Dashboard aggregates target MarketData group (not Clients.All).
    /// </summary>
    private async Task PushDashboardUpdateAsync(List<ArbitragePosition> openPositions, bool botEnabled)
    {
        try
        {
            var totalPnl = openPositions.Sum(p => p.AccumulatedFunding);
            var bestSpread = openPositions.Count > 0
                ? openPositions.Max(p => p.CurrentSpreadPerHour)
                : 0m;

            var dto = new DashboardDto
            {
                BotEnabled        = botEnabled,
                OpenPositionCount = openPositions.Count,
                TotalPnl          = totalPnl,
                BestSpread        = bestSpread,
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
    /// H3: Accepts pre-fetched positions to avoid redundant GetOpenAsync calls.
    /// H-BO1: Position updates target per-user groups to prevent data leaks.
    /// H8: Uses Task.WhenAll with per-item try/catch so one failed push does not drop the rest.
    /// </summary>
    private async Task PushPositionUpdatesAsync(List<ArbitragePosition> openPositions)
    {
        var tasks = openPositions.Select(async pos =>
        {
            try
            {
                var dto = MapPositionToDto(pos);
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
    /// Pushes a ReceiveAlert for recent unread alerts to per-user SignalR groups.
    /// M12: Uses GetRecentUnreadAsync instead of GetAllAsync + in-memory filter.
    /// M7: Uses rolling _lastAlertPushUtc cutoff to avoid replaying alerts across cycles.
    /// C4: Targets per-user groups instead of Clients.All.
    /// </summary>
    private async Task PushNewAlertsAsync(IUnitOfWork uow)
    {
        try
        {
            // M1+M2: Capture both timestamps once; move marker update AFTER query so alerts
            // created between marker update and query return are not silently missed.
            var since = _lastAlertPushUtc;
            var now = DateTime.UtcNow;
            var window = now - since;

            var recentAlerts = await uow.Alerts.GetRecentUnreadAsync(window);
            _lastAlertPushUtc = now;

            foreach (var alert in recentAlerts)
            {
                var dto = new AlertDto
                {
                    Id                  = alert.Id,
                    UserId              = alert.UserId,
                    ArbitragePositionId = alert.ArbitragePositionId,
                    Type                = alert.Type,
                    Severity            = alert.Severity,
                    Message             = alert.Message,
                    IsRead              = alert.IsRead,
                    CreatedAt           = alert.CreatedAt,
                };

                // C4: Send alerts only to the user they belong to
                await _hubContext.Clients.Group($"user-{alert.UserId}").ReceiveAlert(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push alert updates via SignalR");
        }
    }

    // M-BO2: Dispose SemaphoreSlim to release kernel resources
    public override void Dispose()
    {
        if (!_disposed)
        {
            _cycleLock.Dispose();
            _disposed = true;
        }
        base.Dispose();
    }

    private static PositionSummaryDto MapPositionToDto(ArbitragePosition pos)
    {
        return new PositionSummaryDto
        {
            Id                  = pos.Id,
            AssetSymbol         = pos.Asset?.Symbol ?? "?",
            LongExchangeName    = pos.LongExchange?.Name ?? "?",
            ShortExchangeName   = pos.ShortExchange?.Name ?? "?",
            SizeUsdc            = pos.SizeUsdc,
            MarginUsdc          = pos.MarginUsdc,
            EntrySpreadPerHour  = pos.EntrySpreadPerHour,
            CurrentSpreadPerHour = pos.CurrentSpreadPerHour,
            AccumulatedFunding  = pos.AccumulatedFunding,
            UnrealizedPnl       = pos.AccumulatedFunding, // best estimate until live mark-to-market
            RealizedPnl         = pos.RealizedPnl,
            Status              = pos.Status,
            OpenedAt            = pos.OpenedAt,
            ClosedAt            = pos.ClosedAt,
        };
    }
}
