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

    public AnalyticsController(IUnitOfWork uow, ITradeAnalyticsService tradeAnalytics)
    {
        _uow = uow;
        _tradeAnalytics = tradeAnalytics;
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
}
