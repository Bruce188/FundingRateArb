using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
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
    public async Task<IActionResult> Index(int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var effectiveUserId = User.IsInRole("Admin") ? null : userId;
        var summaries = await _tradeAnalytics.GetAllPositionAnalyticsAsync(effectiveUserId, skip, take, ct);

        var vm = new PositionAnalyticsIndexViewModel
        {
            Summaries = summaries,
            Skip = skip,
            Take = take,
            HasMore = summaries.Count == take,
        };

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> PositionAnalysis(int id, CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var effectiveUserId = User.IsInRole("Admin") ? null : userId;
        var analytics = await _tradeAnalytics.GetPositionAnalyticsAsync(id, effectiveUserId, ct);
        if (analytics is null) return NotFound();

        return View(analytics);
    }

    [HttpGet]
    public async Task<IActionResult> PassedOpportunities(int days = 1, int skip = 0, int take = 100, CancellationToken ct = default)
    {
        var from = DateTime.UtcNow.AddDays(-days);
        var to = DateTime.UtcNow;

        var snapshots = await _uow.OpportunitySnapshots.GetRecentAsync(from, to, skip, take, ct);

        var vm = new PassedOpportunitiesViewModel
        {
            Snapshots = snapshots,
            Days = days,
            Skip = skip,
            Take = take,
            HasMore = snapshots.Count == take,
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
            return (null, null);

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return (new HashSet<int>(), new HashSet<int>());

        var assetIds = (await _userSettings.GetUserEnabledAssetIdsAsync(userId)).ToHashSet();
        var exchangeIds = (await _userSettings.GetUserEnabledExchangeIdsAsync(userId)).ToHashSet();
        return (assetIds, exchangeIds);
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
            return Forbid();

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
            var trends = await _rateAnalytics.GetRateTrendsAsync(assetId, days, ct);
            if (scopeExchangeIds is not null)
                trends = trends.Where(t => scopeExchangeIds.Contains(t.ExchangeId)).ToList();
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
            return Forbid();

        // F17: Validate exchangeId against scope before calling service
        if (exchangeId.HasValue && !IsExchangeInScope(exchangeId.Value, scopeExchangeIds))
            return Forbid();

        var trends = await _rateAnalytics.GetRateTrendsAsync(assetId, days, ct);
        if (exchangeId.HasValue)
            trends = trends.Where(t => t.ExchangeId == exchangeId.Value).ToList();
        if (scopeExchangeIds is not null)
            trends = trends.Where(t => scopeExchangeIds.Contains(t.ExchangeId)).ToList();

        return Json(trends);
    }

    [HttpGet]
    public async Task<IActionResult> Correlation(int assetId, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 30);
        var (scopeAssetIds, scopeExchangeIds) = await GetUserScopeAsync();

        // F6: Validate asset scope
        if (!IsAssetInScope(assetId, scopeAssetIds))
            return Forbid();

        var correlations = await _rateAnalytics.GetCrossExchangeCorrelationAsync(assetId, days, ct);
        var asset = await _uow.Assets.GetByIdAsync(assetId);

        // Filter correlations to only include user-enabled exchanges
        if (scopeExchangeIds is not null)
        {
            var exchangeLookup = (await _uow.Exchanges.GetActiveAsync()).ToDictionary(e => e.Name, e => e.Id);
            correlations = correlations
                .Where(c => exchangeLookup.TryGetValue(c.Exchange1, out var e1Id) && scopeExchangeIds.Contains(e1Id)
                         && exchangeLookup.TryGetValue(c.Exchange2, out var e2Id) && scopeExchangeIds.Contains(e2Id))
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
            return Forbid();
        if (!IsExchangeInScope(exchangeId, scopeExchangeIds))
            return Forbid();

        var patterns = await _rateAnalytics.GetTimeOfDayPatternsAsync(assetId, exchangeId, days, ct);
        return Json(patterns);
    }

    [HttpGet]
    public async Task<IActionResult> ZScoreAlerts(decimal threshold = 2.0m, CancellationToken ct = default)
    {
        // F15: Clamp threshold to prevent unbounded values
        threshold = Math.Clamp(threshold, 0.5m, 10.0m);
        var (scopeAssetIds, scopeExchangeIds) = await GetUserScopeAsync();

        var alerts = await _rateAnalytics.GetZScoreAlertsAsync(threshold, ct);

        // F6: Filter Z-score alerts by user scope
        if (scopeAssetIds is not null || scopeExchangeIds is not null)
        {
            var allAssets = await _uow.Assets.GetActiveAsync();
            var allExchanges = await _uow.Exchanges.GetActiveAsync();
            var assetLookup = allAssets.ToDictionary(a => a.Symbol, a => a.Id);
            var exchangeLookup = allExchanges.ToDictionary(e => e.Name, e => e.Id);
            alerts = alerts
                .Where(a => scopeAssetIds is null || (assetLookup.TryGetValue(a.AssetSymbol, out var aid) && scopeAssetIds.Contains(aid)))
                .Where(a => scopeExchangeIds is null || (exchangeLookup.TryGetValue(a.ExchangeName, out var eid) && scopeExchangeIds.Contains(eid)))
                .ToList();
        }

        return Json(alerts);
    }
}
