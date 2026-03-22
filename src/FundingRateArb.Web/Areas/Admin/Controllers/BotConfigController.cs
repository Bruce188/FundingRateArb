using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FundingRateArb.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = "Admin")]
public class BotConfigController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IConfigValidator _configValidator;

    public BotConfigController(IUnitOfWork uow, IConfigValidator configValidator)
    {
        _uow = uow;
        _configValidator = configValidator;
    }

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
            BreakevenHoursMax = config.BreakevenHoursMax,
            AllocationStrategy = config.AllocationStrategy,
            AllocationTopN = config.AllocationTopN,
            FeeAmortizationHours = config.FeeAmortizationHours,
            MinPositionSizeUsdc = config.MinPositionSizeUsdc,
            MinVolume24hUsdc = config.MinVolume24hUsdc,
            RateStalenessMinutes = config.RateStalenessMinutes,
            DailyDrawdownPausePct = config.DailyDrawdownPausePct,
            ConsecutiveLossPause = config.ConsecutiveLossPause,
            FundingWindowMinutes = config.FundingWindowMinutes
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(BotConfigViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var config = await _uow.BotConfig.GetActiveTrackedAsync();

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
        config.AllocationStrategy = model.AllocationStrategy!.Value;
        config.AllocationTopN = model.AllocationTopN!.Value;
        config.FeeAmortizationHours = model.FeeAmortizationHours!.Value;
        config.MinPositionSizeUsdc = model.MinPositionSizeUsdc!.Value;
        config.MinVolume24hUsdc = model.MinVolume24hUsdc!.Value;
        config.RateStalenessMinutes = model.RateStalenessMinutes!.Value;
        config.DailyDrawdownPausePct = model.DailyDrawdownPausePct!.Value;
        config.ConsecutiveLossPause = model.ConsecutiveLossPause!.Value;
        config.FundingWindowMinutes = model.FundingWindowMinutes!.Value;

        var validation = _configValidator.Validate(config);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                ModelState.AddModelError(string.Empty, error);
            return View(model);
        }

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
        var config = await _uow.BotConfig.GetActiveTrackedAsync();
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
