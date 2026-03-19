using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class BotConfigController : Controller
{
    private readonly IUnitOfWork _uow;

    public BotConfigController(IUnitOfWork uow) => _uow = uow;

    public async Task<IActionResult> Index()
    {
        var config = await _uow.BotConfig.GetActiveAsync();

        var model = new BotConfigViewModel
        {
            IsEnabled = config.IsEnabled,
            OpenThreshold = config.OpenThreshold,
            CloseThreshold = config.CloseThreshold,
            AlertThreshold = config.AlertThreshold,
            TotalCapitalUsdc = config.TotalCapitalUsdc,
            DefaultLeverage = config.DefaultLeverage,
            MaxConcurrentPositions = config.MaxConcurrentPositions,
            MaxCapitalPerPosition = config.MaxCapitalPerPosition,
            StopLossPct = config.StopLossPct,
            MaxHoldTimeHours = config.MaxHoldTimeHours,
            VolumeFraction = config.VolumeFraction,
            BreakevenHoursMax = config.BreakevenHoursMax
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(BotConfigViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var config = await _uow.BotConfig.GetActiveAsync();

        config.IsEnabled = model.IsEnabled;
        config.OpenThreshold = model.OpenThreshold!.Value;
        config.CloseThreshold = model.CloseThreshold!.Value;
        config.AlertThreshold = model.AlertThreshold!.Value;
        config.TotalCapitalUsdc = model.TotalCapitalUsdc!.Value;
        config.DefaultLeverage = model.DefaultLeverage!.Value;
        config.MaxConcurrentPositions = model.MaxConcurrentPositions!.Value;
        config.MaxCapitalPerPosition = model.MaxCapitalPerPosition!.Value;
        config.StopLossPct = model.StopLossPct!.Value;
        config.MaxHoldTimeHours = model.MaxHoldTimeHours!.Value;
        config.VolumeFraction = model.VolumeFraction!.Value;
        config.BreakevenHoursMax = model.BreakevenHoursMax!.Value;
        config.LastUpdatedAt = DateTime.UtcNow;
        config.UpdatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "system";

        _uow.BotConfig.Update(config);
        await _uow.SaveAsync();

        TempData["Success"] = "Bot configuration saved successfully.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle()
    {
        var config = await _uow.BotConfig.GetActiveAsync();
        config.IsEnabled = !config.IsEnabled;
        config.LastUpdatedAt = DateTime.UtcNow;
        config.UpdatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "system";

        _uow.BotConfig.Update(config);
        await _uow.SaveAsync();

        var status = config.IsEnabled ? "enabled" : "disabled";
        TempData["Success"] = $"Bot {status} successfully.";
        return RedirectToAction(nameof(Index));
    }
}
