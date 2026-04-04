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

    public ExchangeAnalyticsController(IExchangeAnalyticsService analytics)
        => _analytics = analytics;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = new ExchangeAnalyticsViewModel
        {
            Exchanges = await _analytics.GetExchangeOverviewAsync(ct),
            TopOpportunities = await _analytics.GetTopOpportunitiesAsync(ct: ct),
            RateComparisons = await _analytics.GetRateComparisonsAsync(ct),
            DiscoveryEvents = await _analytics.GetRecentDiscoveryEventsAsync(ct: ct)
        };
        return View(vm);
    }
}
