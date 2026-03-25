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

        // B1: Use lightweight projection to avoid loading full entity graphs with 3 Include joins.
        // Only scalar fields needed for KPI computation are fetched from the database.
        var since = DateTime.UtcNow.AddDays(-days);
        var closedProjections = await _uow.Positions.GetClosedKpiProjectionSinceAsync(since, effectiveUserId, maxRows: 10_000, ct);

        // N1: Single-pass accumulator for all scalar KPIs — avoids ~9 separate list traversals
        var now = DateTime.UtcNow;
        var cutoff7d = now.AddDays(-7);
        var cutoff30d = now.AddDays(-30);

        var totalPnl = 0m;
        var pnl7d = 0m;
        var pnl30d = 0m;
        var winCount = 0;
        var totalHoldHours = 0.0;
        var bestPnl = decimal.MinValue;
        var worstPnl = decimal.MaxValue;
        var kpiCount = 0;

        // Per-asset and per-exchange accumulators
        var assetAccum = new Dictionary<string, (decimal pnl, int trades, int wins)>();
        var exchangeAccum = new Dictionary<string, (decimal pnl, int trades, int wins)>();

        foreach (var p in closedProjections)
        {
            if (!p.RealizedPnl.HasValue)
            {
                continue;
            }

            var pnl = p.RealizedPnl.Value;
            kpiCount++;
            totalPnl += pnl;
            if (pnl > 0)
            {
                winCount++;
            }

            if (pnl > bestPnl)
            {
                bestPnl = pnl;
            }

            if (pnl < worstPnl)
            {
                worstPnl = pnl;
            }

            totalHoldHours += (p.ClosedAt - p.OpenedAt)?.TotalHours ?? 0;

            if (p.ClosedAt >= cutoff7d)
            {
                pnl7d += pnl;
            }

            if (p.ClosedAt >= cutoff30d)
            {
                pnl30d += pnl;
            }

            // Per-asset accumulation
            var assetKey = p.AssetSymbol;
            if (assetAccum.TryGetValue(assetKey, out var assetData))
            {
                assetAccum[assetKey] = (assetData.pnl + pnl, assetData.trades + 1, assetData.wins + (pnl > 0 ? 1 : 0));
            }
            else
            {
                assetAccum[assetKey] = (pnl, 1, pnl > 0 ? 1 : 0);
            }

            // Per-exchange pair accumulation
            var exchangeKey = $"{p.LongExchangeName}/{p.ShortExchangeName}";
            if (exchangeAccum.TryGetValue(exchangeKey, out var exchData))
            {
                exchangeAccum[exchangeKey] = (exchData.pnl + pnl, exchData.trades + 1, exchData.wins + (pnl > 0 ? 1 : 0));
            }
            else
            {
                exchangeAccum[exchangeKey] = (pnl, 1, pnl > 0 ? 1 : 0);
            }
        }

        var vm = new PositionAnalyticsIndexViewModel
        {
            Summaries = summaries,
            Skip = skip,
            Take = take,
            HasMore = summaries.Count == take,
            TotalTrades = kpiCount,
            TotalRealizedPnl = totalPnl,
            TotalRealizedPnl7d = pnl7d,
            TotalRealizedPnl30d = pnl30d,
            WinRate = kpiCount > 0 ? (decimal)winCount / kpiCount : 0,
            AvgHoldTimeHours = kpiCount > 0 ? (decimal)totalHoldHours / kpiCount : 0,
            AvgPnlPerTrade = kpiCount > 0 ? totalPnl / kpiCount : 0,
            BestTradePnl = kpiCount > 0 ? bestPnl : 0,
            WorstTradePnl = kpiCount > 0 ? worstPnl : 0,
            PerAsset = assetAccum
                .Select(kvp => new AssetPerformance
                {
                    AssetSymbol = kvp.Key,
                    Trades = kvp.Value.trades,
                    TotalPnl = kvp.Value.pnl,
                    WinRate = (decimal)kvp.Value.wins / kvp.Value.trades,
                    AvgPnl = kvp.Value.pnl / kvp.Value.trades,
                })
                .OrderByDescending(a => a.TotalPnl)
                .ToList(),
            PerExchangePair = exchangeAccum
                .Select(kvp => new ExchangePairPerformance
                {
                    Pair = kvp.Key,
                    Trades = kvp.Value.trades,
                    TotalPnl = kvp.Value.pnl,
                    WinRate = (decimal)kvp.Value.wins / kvp.Value.trades,
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
