using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.ViewModels;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class StatusPageAggregator(
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    ILogger<StatusPageAggregator> logger
) : IStatusPageAggregator
{
    // Skip-reason histogram (section 8) is deferred: ISignalEngine does not yet expose a
    // synchronous diagnostics snapshot. The section renders a placeholder until a future
    // iteration adds ISignalEngine.GetLastDiagnostics() and wires it here.
    private const string CacheKey = "admin:status:viewmodel";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    public async Task<StatusViewModel> GetAsync(CancellationToken ct)
    {
        // Forward CancellationToken.None into the factory: factory outlives the calling
        // request; one client disconnect must not 500 concurrent waiters.
        // (Mirrors DashboardController:117-119 rationale.)
        var vm = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await BuildAsync(CancellationToken.None);
        });
        return vm!;
    }

    private async Task<StatusViewModel> BuildAsync(CancellationToken ct)
    {
        try
        {
            // Fan out 8 parallel scoped queries.
            var botStateTask = RunInScopeAsync(BuildBotStateAsync, ct);
            var pnlTask = RunInScopeAsync(BuildPnlAttributionAsync, ct);
            var holdTimeTask = RunInScopeAsync(BuildHoldTimeAsync, ct);
            var phantomFeeTask = RunInScopeAsync(BuildPhantomFeeAsync, ct);
            var perPairTask = RunInScopeAsync(BuildPerPairAsync, ct);
            var perAssetTask = RunInScopeAsync(BuildPerAssetAsync, ct);
            var failedOpenTask = RunInScopeAsync(BuildFailedOpenAsync, ct);
            var reconciliationTask = RunInScopeAsync(BuildReconciliationAsync, ct);

            await Task.WhenAll(botStateTask, pnlTask, holdTimeTask, phantomFeeTask,
                               perPairTask, perAssetTask, failedOpenTask, reconciliationTask);

            // Skip-reason histogram comes from in-memory ISignalEngine state, not the DB.
            var skipReasons = BuildSkipReasonsFromDiagnostics();

            return new StatusViewModel
            {
                DatabaseAvailable = true,
                BotState = await botStateTask,
                PnlAttribution = await pnlTask,
                HoldTimeBuckets = await holdTimeTask,
                PhantomFee = await phantomFeeTask,
                PerPairPnl = await perPairTask,
                PerAssetFeeDrag = await perAssetTask,
                FailedOpenEvents = await failedOpenTask,
                SkipReasons = skipReasons,
                Reconciliation = await reconciliationTask,
            };
        }
        catch (DatabaseUnavailableException ex)
        {
            logger.LogWarning(ex, "Status aggregator: DB unavailable");
            return new StatusViewModel { DatabaseAvailable = false, DegradedReason = "Database temporarily unavailable. Try again in a moment." };
        }
        // SqlException (transient errors) are allowed to propagate to the controller catch block,
        // which has access to SqlTransientErrorNumbers (Infrastructure layer).
    }

    private async Task<T> RunInScopeAsync<T>(Func<IUnitOfWork, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var scopedUow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await body(scopedUow, ct);
    }

    private async Task<BotStateHeader> BuildBotStateAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var cfg = await uow.BotConfig.GetActiveAsync();
        var minViable = ThresholdInvariantCalculator.ComputeRequiredOpenFloor(cfg.CloseThreshold, cfg.MinHoldTimeHours);
        return new BotStateHeader
        {
            IsEnabled = cfg.IsEnabled,
            LastUpdatedAt = cfg.LastUpdatedAt,
            OpenThreshold = cfg.OpenThreshold,
            CloseThreshold = cfg.CloseThreshold,
            AlertThreshold = cfg.AlertThreshold,
            StopLossPct = cfg.StopLossPct,
            OpenConfirmTimeoutSeconds = cfg.OpenConfirmTimeoutSeconds,
            MinHoldTimeHours = cfg.MinHoldTimeHours,
            EmergencyCloseSpreadThreshold = cfg.EmergencyCloseSpreadThreshold,
            DefaultLeverage = cfg.DefaultLeverage,
            MaxLeverageCap = cfg.MaxLeverageCap,
            TotalCapitalUsdc = cfg.TotalCapitalUsdc,
            MinViableOpenThreshold = minViable,
            MinViableOpenThresholdViolated = cfg.OpenThreshold < minViable,
        };
    }

    private async Task<List<DTOs.PnlAttributionWindowDto>> BuildPnlAttributionAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windows = new[] { now.AddDays(-7), now.AddDays(-30), DateTime.MinValue };
        return await uow.Positions.GetPnlAttributionWindowsAsync(windows, ct);
    }

    private async Task<List<DTOs.HoldTimeBucketDto>> BuildHoldTimeAsync(IUnitOfWork uow, CancellationToken ct)
        => await uow.Positions.GetHoldTimeBucketsAsync(ct);

    private async Task<PhantomFeeIndicator> BuildPhantomFeeAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var since24 = DateTime.UtcNow.AddHours(-24);
        var since7d = DateTime.UtcNow.AddDays(-7);
        return new PhantomFeeIndicator
        {
            EmergencyClosedZeroFill24h = await uow.Positions.CountEmergencyClosedZeroFillSinceAsync(since24, ct),
            EmergencyClosedZeroFill7d = await uow.Positions.CountEmergencyClosedZeroFillSinceAsync(since7d, ct),
            FailedNullOrderId24h = await uow.Positions.CountPhantomFeeRowsSinceAsync(since24, ct),
        };
    }

    private async Task<List<PerPairPnlRow>> BuildPerPairAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var rows = await uow.Positions.GetPerExchangePairKpiAsync(since, null, ct);
        return rows
            .Select(r => new PerPairPnlRow
            {
                LongExchangeName = r.LongExchangeName,
                ShortExchangeName = r.ShortExchangeName,
                TotalPnl = r.TotalPnl,
                PositionCount = r.Trades,
            })
            .OrderBy(r => r.TotalPnl)
            .ToList();
    }

    private async Task<List<PerAssetFeeDrag>> BuildPerAssetAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var rows = await uow.Positions.GetPerAssetKpiAsync(since, null, ct);
        return rows
            .Where(r => r.Trades >= 2)
            .Select(r => new PerAssetFeeDrag
            {
                AssetSymbol = r.AssetSymbol,
                TotalPnl = r.TotalPnl,
                // AvgFees and AvgFunding not available in AssetKpiAggregateDto; tracked as TODO
                AvgFees = 0m,
                AvgFunding = 0m,
                FeeDragRatio = 0m,
                CloseCount = r.Trades,
            })
            .ToList();
    }

    private async Task<List<DTOs.FailedOpenEventDto>> BuildFailedOpenAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        return await uow.Positions.GetRecentFailedOpensAsync(since, ct);
    }

    private static SkipReasonHistogram BuildSkipReasonsFromDiagnostics()
    {
        // Deferred: ISignalEngine does not yet expose a synchronous diagnostics snapshot.
        // Wire once ISignalEngine.GetLastDiagnostics() is available.
        return new SkipReasonHistogram { Available = false };
    }

    private async Task<ReconciliationSnapshot?> BuildReconciliationAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var report = await uow.ReconciliationReports.GetMostRecentAsync(ct);
        if (report is null)
        {
            return null;
        }

        var snapshot = new ReconciliationSnapshot { Report = report };
        try
        {
            snapshot.PerExchangeEquity = string.IsNullOrWhiteSpace(report.PerExchangeEquityJson)
                ? new Dictionary<string, decimal>()
                : JsonSerializer.Deserialize<Dictionary<string, decimal>>(report.PerExchangeEquityJson);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Reconciliation row {Id}: PerExchangeEquityJson malformed", report.Id);
            snapshot.PerExchangeEquityMalformed = true;
        }
        try
        {
            snapshot.DegradedExchanges = string.IsNullOrWhiteSpace(report.DegradedExchangesJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(report.DegradedExchangesJson);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Reconciliation row {Id}: DegradedExchangesJson malformed", report.Id);
            snapshot.DegradedExchangesMalformed = true;
        }
        return snapshot;
    }
}
