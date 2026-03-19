using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.ViewModels;

namespace FundingRateArb.Web.Controllers;

[Authorize]
public class OpportunitiesController : Controller
{
    private readonly ISignalEngine _signalEngine;
    private readonly IUnitOfWork _uow;

    public OpportunitiesController(ISignalEngine signalEngine, IUnitOfWork uow)
    {
        _signalEngine = signalEngine;
        _uow = uow;
    }

    public async Task<IActionResult> Index(CancellationToken ct = default)
    {
        var opportunities = await _signalEngine.GetOpportunitiesAsync(ct);
        BotConfiguration? config = null;
        try { config = await _uow.BotConfig.GetActiveAsync(); }
        catch (InvalidOperationException) { }

        if (config is null)
            return View(new OpportunityListViewModel { Opportunities = new() });

        var vm = new OpportunityListViewModel
        {
            Opportunities = opportunities,
        };

        // L5: only expose operator capital/leverage parameters to Admins
        if (User.IsInRole("Admin"))
        {
            vm.NotionalPerLeg = config.TotalCapitalUsdc * config.MaxCapitalPerPosition * config.DefaultLeverage;
            vm.VolumeFraction = config.VolumeFraction;
        }

        return View(vm);
    }
}
