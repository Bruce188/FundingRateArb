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
        var summaries = await _tradeAnalytics.GetAllPositionAnalyticsAsync(effectiveUserId, skip, take, ct);

        // Compute summary KPIs from closed positions — bounded to avoid loading full history
        var since = DateTime.UtcNow.AddDays(-days);
        var closedPositions = await _uow.Positions.GetClosedWithNavigationSinceAsync(since, effectiveUserId, ct);

        // Hard cap to prevent excessive memory usage for admin users with large datasets
        var closedWithPnl = closedPositions
            .Where(p => p.RealizedPnl.HasValue)
            .Take(10_000)
            .ToList();
        var now = DateTime.UtcNow;

        var vm = new PositionAnalyticsIndexViewModel
        {
            Summaries = summaries,
            Skip = skip,
            Take = take,
            HasMore = summaries.Count == take,
            TotalTrades = closedWithPnl.Count,
            TotalRealizedPnl = closedWithPnl.Sum(p => p.RealizedPnl ?? 0),
            TotalRealizedPnl7d = closedWithPnl.Where(p => p.ClosedAt >= now.AddDays(-7)).Sum(p => p.RealizedPnl ?? 0),
            TotalRealizedPnl30d = closedWithPnl.Where(p => p.ClosedAt >= now.AddDays(-30)).Sum(p => p.RealizedPnl ?? 0),
            WinRate = closedWithPnl.Count > 0 ? (decimal)closedWithPnl.Count(p => p.RealizedPnl > 0) / closedWithPnl.Count : 0,
            AvgHoldTimeHours = closedWithPnl.Count > 0 ? (decimal)closedWithPnl.Average(p => (p.ClosedAt - p.OpenedAt)?.TotalHours ?? 0) : 0,
            AvgPnlPerTrade = closedWithPnl.Count > 0 ? closedWithPnl.Average(p => p.RealizedPnl ?? 0) : 0,
            BestTradePnl = closedWithPnl.Count > 0 ? closedWithPnl.Max(p => p.RealizedPnl ?? 0) : 0,
            WorstTradePnl = closedWithPnl.Count > 0 ? closedWithPnl.Min(p => p.RealizedPnl ?? 0) : 0,
            PerAsset = closedWithPnl
                .GroupBy(p => p.Asset?.Symbol ?? "Unknown")
                .Select(g =>
                {
                    var count = g.Count();
                    return new AssetPerformance
                    {
                        AssetSymbol = g.Key,
                        Trades = count,
                        TotalPnl = g.Sum(p => p.RealizedPnl ?? 0),
                        WinRate = (decimal)g.Count(p => p.RealizedPnl > 0) / count,
                        AvgPnl = g.Average(p => p.RealizedPnl ?? 0),
                    };
                })
                .OrderByDescending(a => a.TotalPnl)
                .ToList(),
            PerExchangePair = closedWithPnl
                .GroupBy(p => $"{p.LongExchange?.Name ?? "?"}/{p.ShortExchange?.Name ?? "?"}")
                .Select(g =>
                {
                    var count = g.Count();
                    return new ExchangePairPerformance
                    {
                        Pair = g.Key,
                        Trades = count,
                        TotalPnl = g.Sum(p => p.RealizedPnl ?? 0),
                        WinRate = (decimal)g.Count(p => p.RealizedPnl > 0) / count,
                    };
                })
                .OrderByDescending(e => e.TotalPnl)
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

        var snapshots = await _uow.OpportunitySnapshots.GetRecentAsync(from, to, skip, take, ct);

        // Compute skip reason distribution via SQL aggregate (single query, no double-fetch)
        var (totalSeen, opened, skipReasonDict) = await _uow.OpportunitySnapshots.GetSkipReasonStatsAsync(from, to, ct);
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
