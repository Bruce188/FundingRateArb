using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.Common.Repositories;
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

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var opportunities = await _signalEngine.GetOpportunitiesAsync();
        var config = await _uow.BotConfig.GetActiveAsync();
        var vm = new OpportunityListViewModel
        {
            Opportunities   = opportunities,
            NotionalPerLeg  = config.TotalCapitalUsdc * config.MaxCapitalPerPosition * config.DefaultLeverage,
            VolumeFraction  = config.VolumeFraction,
        };
        return View(vm);
    }
}
