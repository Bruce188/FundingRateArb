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

    public ExchangeAnalyticsController(IExchangeAnalyticsService analytics, ICoinGlassAnalyticsRepository analyticsRepo)
    {
        _analytics = analytics;
        _analyticsRepo = analyticsRepo;
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
            DiscoveryEvents = await _analytics.GetRecentDiscoveryEventsAsync(ct: ct)
        };
        return View(vm);
    }
}
