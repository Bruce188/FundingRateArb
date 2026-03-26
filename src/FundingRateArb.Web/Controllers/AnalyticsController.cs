using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class AnalyticsController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly ITradeAnalyticsService _tradeAnalytics;
    private readonly IRateAnalyticsService _rateAnalytics;
    private readonly IUserSettingsService _userSettings;

    public AnalyticsController(IUnitOfWork uow, ITradeAnalyticsService tradeAnalytics, IRateAnalyticsService rateAnalytics, IUserSettingsService userSettings)
    {
        _uow = uow;
        _tradeAnalytics = tradeAnalytics;
        _rateAnalytics = rateAnalytics;
        _userSettings = userSettings;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int skip = 0, int take = 50, int days = 90, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        days = Math.Clamp(days, 1, 365);
        var effectiveUserId = User.IsInRole("Admin") ? null : userId;
        var since = DateTime.UtcNow.AddDays(-days);

        // NB6: Push KPI aggregation to SQL — avoids materializing up to 10K rows for in-memory computation.
        // Run all independent queries concurrently.
        var summariesTask = _tradeAnalytics.GetAllPositionAnalyticsAsync(effectiveUserId, skip, take, ct);
        var kpiTask = _uow.Positions.GetKpiAggregatesAsync(since, effectiveUserId, ct);
        var perAssetTask = _uow.Positions.GetPerAssetKpiAsync(since, effectiveUserId, ct);
        var perExchangeTask = _uow.Positions.GetPerExchangePairKpiAsync(since, effectiveUserId, ct);
        await Task.WhenAll(summariesTask, kpiTask, perAssetTask, perExchangeTask);

        var summaries = summariesTask.Result;
        var kpi = kpiTask.Result;
        var perAsset = perAssetTask.Result;
        var perExchange = perExchangeTask.Result;

        var vm = new PositionAnalyticsIndexViewModel
        {
            Summaries = summaries,
            Skip = skip,
            Take = take,
            HasMore = summaries.Count == take,
            TotalTrades = kpi.TotalTrades,
            TotalRealizedPnl = kpi.TotalPnl,
            TotalRealizedPnl7d = kpi.Pnl7d,
            TotalRealizedPnl30d = kpi.Pnl30d,
            WinRate = kpi.TotalTrades > 0 ? (decimal)kpi.WinCount / kpi.TotalTrades : 0,
            AvgHoldTimeHours = kpi.TotalTrades > 0 ? (decimal)kpi.TotalHoldHours / kpi.TotalTrades : 0,
            AvgPnlPerTrade = kpi.TotalTrades > 0 ? kpi.TotalPnl / kpi.TotalTrades : 0,
            BestTradePnl = kpi.TotalTrades > 0 ? kpi.BestPnl : 0,
            WorstTradePnl = kpi.TotalTrades > 0 ? kpi.WorstPnl : 0,
            PerAsset = perAsset
                .Select(a => new AssetPerformance
                {
                    AssetSymbol = a.AssetSymbol,
                    Trades = a.Trades,
                    TotalPnl = a.TotalPnl,
                    WinRate = a.Trades > 0 ? (decimal)a.WinCount / a.Trades : 0,
                    AvgPnl = a.Trades > 0 ? a.TotalPnl / a.Trades : 0,
                })
                .ToList(),
            PerExchangePair = perExchange
                .Select(e => new ExchangePairPerformance
                {
                    Pair = $"{e.LongExchangeName}/{e.ShortExchangeName}",
                    Trades = e.Trades,
                    TotalPnl = e.TotalPnl,
                    WinRate = e.Trades > 0 ? (decimal)e.WinCount / e.Trades : 0,
                })
                .ToList(),
        };

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> PositionAnalysis(int id, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return Unauthorized();
        }

        var effectiveUserId = User.IsInRole("Admin") ? null : userId;
        var analytics = await _tradeAnalytics.GetPositionAnalyticsAsync(id, effectiveUserId, ct);
        if (analytics is null)
        {
            return NotFound();
        }

        return View(analytics);
    }

    [HttpGet]
    public async Task<IActionResult> PassedOpportunities(int days = 1, int skip = 0, int take = 100, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);

        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        // N8: Run independent queries concurrently
        var snapshotsTask = _uow.OpportunitySnapshots.GetRecentAsync(from, to, skip, take, ct);
        var statsTask = _uow.OpportunitySnapshots.GetSkipReasonStatsAsync(from, to, ct);
        await Task.WhenAll(snapshotsTask, statsTask);

        var snapshots = snapshotsTask.Result;
        // Compute skip reason distribution via SQL aggregate (single query, no double-fetch)
        var (totalSeen, opened, skipReasonDict) = statsTask.Result;
        var skipReasons = skipReasonDict
            .Select(kvp => new SkipReasonStat { Reason = kvp.Key, Count = kvp.Value })
            .OrderByDescending(s => s.Count)
            .ToList();

        var vm = new PassedOpportunitiesViewModel
        {
            Snapshots = snapshots,
            Days = days,
            Skip = skip,
            Take = take,
            HasMore = snapshots.Count == take,
            TotalOpportunitiesSeen = totalSeen,
            TotalOpened = opened,
            OpenedPct = totalSeen > 0 ? (decimal)opened / totalSeen * 100 : 0,
            TopSkipReason = skipReasons.FirstOrDefault()?.Reason ?? "N/A",
            SkipReasons = skipReasons,
        };

        return View(vm);
    }

    // ── Rate Analytics ───────────────────────────────────────────

    /// <summary>
    /// Returns the user's enabled asset/exchange IDs, or null for admins (no filter).
    /// </summary>
    private async Task<(HashSet<int>? AssetIds, HashSet<int>? ExchangeIds)> GetUserScopeAsync()
    {
        if (User.IsInRole("Admin"))
        {
            return (null, null);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
        {
            return (new HashSet<int>(), new HashSet<int>());
        }

        var assetIds = (await _userSettings.GetUserEnabledAssetIdsAsync(userId)).ToHashSet();
        var exchangeIds = (await _userSettings.GetUserEnabledExchangeIdsAsync(userId)).ToHashSet();
        return (assetIds, exchangeIds);
    }

    /// <summary>
    /// Returns the user's scope with resolved names for filtering DTOs that contain string identifiers.
    /// Avoids redundant DB round-trips by loading assets/exchanges once and mapping IDs to names.
    /// </summary>
    private async Task<(HashSet<int>? AssetIds, HashSet<int>? ExchangeIds, HashSet<string>? AssetSymbols, HashSet<string>? ExchangeNames)> GetUserScopeWithNamesAsync()
    {
        var (assetIds, exchangeIds) = await GetUserScopeAsync();
        if (assetIds is null && exchangeIds is null)
        {
            return (null, null, null, null);
        }

        var assets = await _uow.Assets.GetActiveAsync();
        var exchanges = await _uow.Exchanges.GetActiveAsync();

        var assetSymbols = assetIds is not null
            ? assets.Where(a => assetIds.Contains(a.Id)).Select(a => a.Symbol).ToHashSet()
            : null;
        var exchangeNames = exchangeIds is not null
            ? exchanges.Where(e => exchangeIds.Contains(e.Id)).Select(e => e.Name).ToHashSet()
            : null;

        return (assetIds, exchangeIds, assetSymbols, exchangeNames);
    }

    /// <summary>
    /// F6: Validates that the given assetId is within the user's enabled scope.
    /// Returns true if valid (or admin), false if out of scope.
    /// </summary>
    private static bool IsAssetInScope(int assetId, HashSet<int>? scopeAssetIds)
    {
        return scopeAssetIds is null || scopeAssetIds.Contains(assetId);
    }

    /// <summary>
    /// F6: Validates that the given exchangeId is within the user's enabled scope.
    /// </summary>
    private static bool IsExchangeInScope(int exchangeId, HashSet<int>? scopeExchangeIds)
    {
        return scopeExchangeIds is null || scopeExchangeIds.Contains(exchangeId);
    }

    [HttpGet]
    public async Task<IActionResult> RateAnalytics(int? assetId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var (scopeAssetIds, scopeExchangeIds) = await GetUserScopeAsync();

        if (assetId.HasValue && !IsAssetInScope(assetId.Value, scopeAssetIds))
        {
            return Forbid();
        }

        // F4: Fetch assets and exchanges once, reuse for dropdown + scope filtering
        var assets = await _uow.Assets.GetActiveAsync();
        var exchanges = await _uow.Exchanges.GetActiveAsync();

        var filteredAssets = scopeAssetIds is not null
            ? assets.Where(a => scopeAssetIds.Contains(a.Id)).ToList()
            : assets;

        var vm = new RateAnalyticsViewModel
        {
            SelectedAssetId = assetId,
            SelectedDays = days,
            AvailableAssets = filteredAssets.Select(a => new AssetOption { Id = a.Id, Symbol = a.Symbol }).ToList(),
        };

        if (assetId.HasValue)
        {
            var trends = await _rateAnalytics.GetRateTrendsAsync(assetId, days, ct: ct);
            if (scopeExchangeIds is not null)
            {
                trends = trends.Where(t => scopeExchangeIds.Contains(t.ExchangeId)).ToList();
            }

            vm.Trends = trends;
        }

        // Z-score alerts are lazy-loaded via AJAX from /Analytics/ZScoreAlerts

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> RateTrendData(int assetId, int? exchangeId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var (scopeAssetIds, scopeExchangeIds) = await GetUserScopeAsync();

        if (!IsAssetInScope(assetId, scopeAssetIds))
        {
            return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        }

        // F17: Validate exchangeId against scope before calling service
        if (exchangeId.HasValue && !IsExchangeInScope(exchangeId.Value, scopeExchangeIds))
        {
            return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        }

        // Pass exchangeId to service for server-side filtering (avoids fetching all exchanges)
        var trends = await _rateAnalytics.GetRateTrendsAsync(assetId, days, exchangeId, ct);
        if (scopeExchangeIds is not null)
        {
            trends = trends.Where(t => scopeExchangeIds.Contains(t.ExchangeId)).ToList();
        }

        return Json(trends);
    }

    [HttpGet]
    public async Task<IActionResult> Correlation(int assetId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var (scopeAssetIds, _, _, scopeExchangeNames) = await GetUserScopeWithNamesAsync();

        // F6: Validate asset scope
        if (!IsAssetInScope(assetId, scopeAssetIds))
        {
            return Forbid();
        }

        var correlations = await _rateAnalytics.GetCrossExchangeCorrelationAsync(assetId, days, ct);
        var asset = await _uow.Assets.GetByIdAsync(assetId);

        // Filter correlations using resolved exchange names — single DB call via scope helper
        if (scopeExchangeNames is not null)
        {
            correlations = correlations
                .Where(c => scopeExchangeNames.Contains(c.Exchange1)
                         && scopeExchangeNames.Contains(c.Exchange2))
                .ToList();
        }

        var vm = new CorrelationViewModel
        {
            Correlations = correlations,
            AssetId = assetId,
            AssetSymbol = asset?.Symbol ?? $"#{assetId}",
            Days = days,
        };

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> TimeOfDayData(int assetId, int exchangeId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var (scopeAssetIds, scopeExchangeIds) = await GetUserScopeAsync();

        // F6: Validate both asset and exchange scope
        if (!IsAssetInScope(assetId, scopeAssetIds))
        {
            return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        }

        if (!IsExchangeInScope(exchangeId, scopeExchangeIds))
        {
            return new JsonResult(new { error = "Access denied" }) { StatusCode = 403 };
        }

        var patterns = await _rateAnalytics.GetTimeOfDayPatternsAsync(assetId, exchangeId, days, ct);
        return Json(patterns);
    }

    [HttpGet]
    public async Task<IActionResult> ZScoreAlerts(decimal threshold = 2.0m, CancellationToken ct = default)
    {
        // F15: Clamp threshold to prevent unbounded values
        threshold = Math.Clamp(threshold, 0.5m, 10.0m);
        var (_, _, scopeAssetSymbols, scopeExchangeNames) = await GetUserScopeWithNamesAsync();

        var alerts = await _rateAnalytics.GetZScoreAlertsAsync(threshold, ct);

        // Filter by scope using resolved names — single DB call via GetUserScopeWithNamesAsync
        if (scopeAssetSymbols is not null || scopeExchangeNames is not null)
        {
            alerts = alerts
                .Where(a => scopeAssetSymbols is null || scopeAssetSymbols.Contains(a.AssetSymbol))
                .Where(a => scopeExchangeNames is null || scopeExchangeNames.Contains(a.ExchangeName))
                .ToList();
        }

        return Json(alerts);
    }
}
