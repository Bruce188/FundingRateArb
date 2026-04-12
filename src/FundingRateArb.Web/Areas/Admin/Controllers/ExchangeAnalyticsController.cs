using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class ExchangeAnalyticsController : Controller
{
    private readonly IExchangeAnalyticsService _analytics;
    private readonly ICoinGlassAnalyticsRepository _analyticsRepo;
    private readonly ICoinGlassScreeningProvider _screeningProvider;
    private readonly ILogger<ExchangeAnalyticsController> _logger;

    public ExchangeAnalyticsController(
        IExchangeAnalyticsService analytics,
        ICoinGlassAnalyticsRepository analyticsRepo,
        ICoinGlassScreeningProvider screeningProvider,
        ILogger<ExchangeAnalyticsController> logger)
    {
        _analytics = analytics;
        _analyticsRepo = analyticsRepo;
        _screeningProvider = screeningProvider;
        _logger = logger;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Fetch latest snapshot once and pass to methods that need it
        var latestRates = await _analyticsRepo.GetLatestSnapshotPerExchangeAsync(ct);

        var vm = new ExchangeAnalyticsViewModel
        {
            Exchanges = await _analytics.GetExchangeOverviewAsync(latestRates, ct),
            TopOpportunities = await _analytics.GetTopOpportunitiesAsync(latestRates, ct: ct),
            RateComparisons = await _analytics.GetRateComparisonsAsync(ct),
            DiscoveryEvents = await _analytics.GetRecentDiscoveryEventsAsync(ct: ct),
            CoinGlassAvailable = _screeningProvider.IsAvailable,
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TriggerFetch(CancellationToken ct)
    {
        try
        {
            var hotSymbols = await _screeningProvider.GetHotSymbolsAsync(ct);
            TempData["Success"] = $"CoinGlass fetch completed. {hotSymbols.Count} hot symbols found.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual CoinGlass fetch failed");
            TempData["Error"] = "CoinGlass fetch failed. Check logs for details.";
        }

        return RedirectToAction(nameof(Index));
    }
}
