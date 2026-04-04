using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;
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
    private readonly ILogger<BotConfigController> _logger;

    public BotConfigController(IUnitOfWork uow, IConfigValidator configValidator, ILogger<BotConfigController> logger)
    {
        _uow = uow;
        _configValidator = configValidator;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var config = await _uow.BotConfig.GetActiveAsync();

        var model = new BotConfigViewModel
        {
            IsEnabled = config.IsEnabled,
            OperatingState = config.OperatingState,
            OpenThreshold = config.OpenThreshold,
            CloseThreshold = config.CloseThreshold,
            AlertThreshold = config.AlertThreshold,
            TotalCapitalUsdc = config.TotalCapitalUsdc,
            DefaultLeverage = config.DefaultLeverage,
            MaxConcurrentPositions = config.MaxConcurrentPositions,
            MaxCapitalPerPosition = config.MaxCapitalPerPosition,
            StopLossPct = config.StopLossPct,
            MaxHoldTimeHours = config.MaxHoldTimeHours,
            MinHoldTimeHours = config.MinHoldTimeHours,
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
            FundingWindowMinutes = config.FundingWindowMinutes,
            MaxExposurePerAsset = config.MaxExposurePerAsset,
            MaxExposurePerExchange = config.MaxExposurePerExchange,
            TargetPnlMultiplier = config.TargetPnlMultiplier,
            AdaptiveHoldEnabled = config.AdaptiveHoldEnabled,
            RebalanceEnabled = config.RebalanceEnabled,
            RebalanceMinImprovement = config.RebalanceMinImprovement,
            MaxRebalancesPerCycle = config.MaxRebalancesPerCycle
        };

        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(BotConfigViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (model.MinHoldTimeHours > model.MaxHoldTimeHours)
        {
            ModelState.AddModelError(nameof(model.MinHoldTimeHours),
                "Minimum hold time must not exceed maximum hold time.");
            return View(model);
        }

        var config = await _uow.BotConfig.GetActiveTrackedAsync();

        // NB2/NB4: Do not set OperatingState from the form — only SetState can change it.
        // Derive IsEnabled from the DB-persisted OperatingState.
        config.IsEnabled = config.OperatingState != BotOperatingState.Stopped;
        config.OpenThreshold = model.OpenThreshold!.Value;
        config.CloseThreshold = model.CloseThreshold!.Value;
        config.AlertThreshold = model.AlertThreshold!.Value;
        config.TotalCapitalUsdc = model.TotalCapitalUsdc!.Value;
        config.DefaultLeverage = model.DefaultLeverage!.Value;
        config.MaxConcurrentPositions = model.MaxConcurrentPositions!.Value;
        config.MaxCapitalPerPosition = model.MaxCapitalPerPosition!.Value;
        config.StopLossPct = model.StopLossPct!.Value;
        config.MaxHoldTimeHours = model.MaxHoldTimeHours!.Value;
        config.MinHoldTimeHours = model.MinHoldTimeHours!.Value;
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
        config.MaxExposurePerAsset = model.MaxExposurePerAsset!.Value;
        config.MaxExposurePerExchange = model.MaxExposurePerExchange!.Value;
        config.TargetPnlMultiplier = model.TargetPnlMultiplier!.Value;
        config.AdaptiveHoldEnabled = model.AdaptiveHoldEnabled;
        config.RebalanceEnabled = model.RebalanceEnabled;
        config.RebalanceMinImprovement = model.RebalanceMinImprovement!.Value;
        config.MaxRebalancesPerCycle = model.MaxRebalancesPerCycle!.Value;

        var validation = _configValidator.Validate(config);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        config.LastUpdatedAt = DateTime.UtcNow;
        config.UpdatedByUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "system";

        _uow.BotConfig.Update(config);
        await _uow.SaveAsync();
        _uow.BotConfig.InvalidateCache();

        _logger.LogWarning("Admin {Action}: {EntityType} {EntityId} by {AdminUserId}",
            "Updated", "BotConfiguration", config.Id, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown");

        TempData["Success"] = "Bot configuration saved successfully.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetState(BotOperatingState newState)
    {
        // B1: Validate enum value is defined
        if (!Enum.IsDefined(newState))
        {
            TempData["Error"] = "Invalid operating state value.";
            return RedirectToAction(nameof(Index));
        }

        var config = await _uow.BotConfig.GetActiveTrackedAsync();
        var oldState = config.OperatingState;

        // Validate: Trading is automatic-only (set by orchestrator)
        if (newState == BotOperatingState.Trading)
        {
            TempData["Success"] = "Note: Trading state is set automatically when a position opens. Use Armed instead.";
            return RedirectToAction(nameof(Index));
        }

        config.OperatingState = newState;
        config.IsEnabled = newState != BotOperatingState.Stopped;
        config.LastUpdatedAt = DateTime.UtcNow;
        config.UpdatedByUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "system";

        _uow.BotConfig.Update(config);
        await _uow.SaveAsync();
        _uow.BotConfig.InvalidateCache();

        _logger.LogWarning("Admin state change: {OldState} -> {NewState} for BotConfiguration {Id} by {AdminUserId}",
            oldState, newState, config.Id, config.UpdatedByUserId);

        TempData["Success"] = $"Bot state changed: {oldState} -> {newState}";
        return RedirectToAction(nameof(Index));
    }

    // Backwards-compatible toggle endpoint
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle()
    {
        var config = await _uow.BotConfig.GetActiveTrackedAsync();
        var oldState = config.OperatingState;
        var newState = oldState == BotOperatingState.Stopped
            ? BotOperatingState.Armed
            : BotOperatingState.Stopped;

        config.OperatingState = newState;
        config.IsEnabled = newState != BotOperatingState.Stopped;
        config.LastUpdatedAt = DateTime.UtcNow;
        config.UpdatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "system";

        await _uow.SaveAsync();
        _uow.BotConfig.InvalidateCache();

        _logger.LogWarning("Admin toggle: {OldState} -> {NewState} for BotConfiguration {Id} by {AdminUserId}",
            oldState, newState, config.Id, config.UpdatedByUserId);

        TempData["Success"] = $"Bot state changed: {oldState} -> {newState}";
        return RedirectToAction(nameof(Index));
    }
}
