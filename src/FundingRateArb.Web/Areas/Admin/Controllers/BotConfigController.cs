using System.Security.Claims;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
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
            MaxRebalancesPerCycle = config.MaxRebalancesPerCycle,
            MaxLeverageCap = config.MaxLeverageCap,
            MarginUtilizationAlertPct = config.MarginUtilizationAlertPct,
            PnlTargetCooldownMinutes = config.PnlTargetCooldownMinutes,
            // Risk Management
            MinHoldBeforePnlTargetMinutes = config.MinHoldBeforePnlTargetMinutes,
            LiquidationWarningPct = config.LiquidationWarningPct,
            LiquidationEarlyWarningPct = config.LiquidationEarlyWarningPct,
            ExchangeCircuitBreakerThreshold = config.ExchangeCircuitBreakerThreshold,
            ExchangeCircuitBreakerMinutes = config.ExchangeCircuitBreakerMinutes,
            PriceFeedFailureCloseThreshold = config.PriceFeedFailureCloseThreshold,
            // Thresholds
            EmergencyCloseSpreadThreshold = config.EmergencyCloseSpreadThreshold,
            MinEdgeMultiplier = config.MinEdgeMultiplier,
            DivergenceAlertMultiplier = config.DivergenceAlertMultiplier,
            DivergenceAlertConfirmationCycles = config.DivergenceAlertConfirmationCycles,
            RotationDivergenceHorizonHours = config.RotationDivergenceHorizonHours,
            PreferCloseOnDivergenceNarrowing = config.PreferCloseOnDivergenceNarrowing,
            SlippageBufferBps = config.SlippageBufferBps,
            StablecoinAlertThresholdPct = config.StablecoinAlertThresholdPct,
            StablecoinCriticalThresholdPct = config.StablecoinCriticalThresholdPct,
            MinConsecutiveFavorableCycles = config.MinConsecutiveFavorableCycles,
            FundingFlipExitCycles = config.FundingFlipExitCycles,
            // Advanced booleans
            UseRiskBasedDivergenceClose = config.UseRiskBasedDivergenceClose,
            UseBreakEvenSizeFilter = config.UseBreakEvenSizeFilter,
            PairAutoDenyEnabled = config.PairAutoDenyEnabled,
            DryRunEnabled = config.DryRunEnabled,
            ForceConcurrentExecution = config.ForceConcurrentExecution,
            // Infrastructure
            ReconciliationIntervalCycles = config.ReconciliationIntervalCycles,
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

        // Validate against a temporary config before mutating the tracked entity,
        // matching the SettingsController pattern for MaxLeverageCap.
        var candidate = new BotConfiguration
        {
            OpenThreshold = model.OpenThreshold!.Value,
            CloseThreshold = model.CloseThreshold!.Value,
            AlertThreshold = model.AlertThreshold!.Value,
            TotalCapitalUsdc = model.TotalCapitalUsdc!.Value,
            DefaultLeverage = model.DefaultLeverage!.Value,
            MaxConcurrentPositions = model.MaxConcurrentPositions!.Value,
            MaxCapitalPerPosition = model.MaxCapitalPerPosition!.Value,
            StopLossPct = model.StopLossPct!.Value,
            MaxHoldTimeHours = model.MaxHoldTimeHours!.Value,
            MinHoldTimeHours = model.MinHoldTimeHours!.Value,
            VolumeFraction = model.VolumeFraction!.Value,
            BreakevenHoursMax = model.BreakevenHoursMax!.Value,
            AllocationStrategy = model.AllocationStrategy!.Value,
            AllocationTopN = model.AllocationTopN!.Value,
            FeeAmortizationHours = model.FeeAmortizationHours!.Value,
            MinPositionSizeUsdc = model.MinPositionSizeUsdc!.Value,
            MinVolume24hUsdc = model.MinVolume24hUsdc!.Value,
            RateStalenessMinutes = model.RateStalenessMinutes!.Value,
            DailyDrawdownPausePct = model.DailyDrawdownPausePct!.Value,
            ConsecutiveLossPause = model.ConsecutiveLossPause!.Value,
            FundingWindowMinutes = model.FundingWindowMinutes!.Value,
            MaxExposurePerAsset = model.MaxExposurePerAsset!.Value,
            MaxExposurePerExchange = model.MaxExposurePerExchange!.Value,
            TargetPnlMultiplier = model.TargetPnlMultiplier!.Value,
            AdaptiveHoldEnabled = model.AdaptiveHoldEnabled,
            RebalanceEnabled = model.RebalanceEnabled,
            RebalanceMinImprovement = model.RebalanceMinImprovement!.Value,
            MaxRebalancesPerCycle = model.MaxRebalancesPerCycle!.Value,
            MaxLeverageCap = model.MaxLeverageCap,
            MarginUtilizationAlertPct = model.MarginUtilizationAlertPct,
            PnlTargetCooldownMinutes = model.PnlTargetCooldownMinutes!.Value,
            // Risk Management
            MinHoldBeforePnlTargetMinutes = model.MinHoldBeforePnlTargetMinutes!.Value,
            LiquidationWarningPct = model.LiquidationWarningPct!.Value,
            LiquidationEarlyWarningPct = model.LiquidationEarlyWarningPct!.Value,
            ExchangeCircuitBreakerThreshold = model.ExchangeCircuitBreakerThreshold!.Value,
            ExchangeCircuitBreakerMinutes = model.ExchangeCircuitBreakerMinutes!.Value,
            PriceFeedFailureCloseThreshold = model.PriceFeedFailureCloseThreshold!.Value,
            // Thresholds
            EmergencyCloseSpreadThreshold = model.EmergencyCloseSpreadThreshold!.Value,
            MinEdgeMultiplier = model.MinEdgeMultiplier!.Value,
            DivergenceAlertMultiplier = model.DivergenceAlertMultiplier!.Value,
            DivergenceAlertConfirmationCycles = model.DivergenceAlertConfirmationCycles!.Value,
            RotationDivergenceHorizonHours = model.RotationDivergenceHorizonHours!.Value,
            PreferCloseOnDivergenceNarrowing = model.PreferCloseOnDivergenceNarrowing,
            SlippageBufferBps = model.SlippageBufferBps!.Value,
            StablecoinAlertThresholdPct = model.StablecoinAlertThresholdPct!.Value,
            StablecoinCriticalThresholdPct = model.StablecoinCriticalThresholdPct!.Value,
            MinConsecutiveFavorableCycles = model.MinConsecutiveFavorableCycles!.Value,
            FundingFlipExitCycles = model.FundingFlipExitCycles!.Value,
            // Advanced booleans
            UseRiskBasedDivergenceClose = model.UseRiskBasedDivergenceClose,
            UseBreakEvenSizeFilter = model.UseBreakEvenSizeFilter,
            PairAutoDenyEnabled = model.PairAutoDenyEnabled,
            DryRunEnabled = model.DryRunEnabled,
            ForceConcurrentExecution = model.ForceConcurrentExecution,
            // Infrastructure
            ReconciliationIntervalCycles = model.ReconciliationIntervalCycles!.Value,
        };

        var validation = _configValidator.Validate(candidate);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(model);
        }

        // Validation passed — now mutate the tracked entity
        var config = await _uow.BotConfig.GetActiveTrackedAsync();

        // NB2/NB4: Do not set OperatingState from the form — only SetState can change it.
        // Derive IsEnabled from the DB-persisted OperatingState.
        config.IsEnabled = config.OperatingState != BotOperatingState.Stopped;
        config.OpenThreshold = candidate.OpenThreshold;
        config.CloseThreshold = candidate.CloseThreshold;
        config.AlertThreshold = candidate.AlertThreshold;
        // TotalCapitalUsdc is derived from live exchange balances — not consumed from form input
        config.DefaultLeverage = candidate.DefaultLeverage;
        config.MaxConcurrentPositions = candidate.MaxConcurrentPositions;
        config.MaxCapitalPerPosition = candidate.MaxCapitalPerPosition;
        config.StopLossPct = candidate.StopLossPct;
        config.MaxHoldTimeHours = candidate.MaxHoldTimeHours;
        config.MinHoldTimeHours = candidate.MinHoldTimeHours;
        config.VolumeFraction = candidate.VolumeFraction;
        config.BreakevenHoursMax = candidate.BreakevenHoursMax;
        config.AllocationStrategy = candidate.AllocationStrategy;
        config.AllocationTopN = candidate.AllocationTopN;
        config.FeeAmortizationHours = candidate.FeeAmortizationHours;
        config.MinPositionSizeUsdc = candidate.MinPositionSizeUsdc;
        config.MinVolume24hUsdc = candidate.MinVolume24hUsdc;
        config.RateStalenessMinutes = candidate.RateStalenessMinutes;
        config.DailyDrawdownPausePct = candidate.DailyDrawdownPausePct;
        config.ConsecutiveLossPause = candidate.ConsecutiveLossPause;
        config.FundingWindowMinutes = candidate.FundingWindowMinutes;
        config.MaxExposurePerAsset = candidate.MaxExposurePerAsset;
        config.MaxExposurePerExchange = candidate.MaxExposurePerExchange;
        config.TargetPnlMultiplier = candidate.TargetPnlMultiplier;
        config.AdaptiveHoldEnabled = candidate.AdaptiveHoldEnabled;
        config.RebalanceEnabled = candidate.RebalanceEnabled;
        config.RebalanceMinImprovement = candidate.RebalanceMinImprovement;
        config.MaxRebalancesPerCycle = candidate.MaxRebalancesPerCycle;
        config.MaxLeverageCap = candidate.MaxLeverageCap;
        config.MarginUtilizationAlertPct = candidate.MarginUtilizationAlertPct;
        config.PnlTargetCooldownMinutes = candidate.PnlTargetCooldownMinutes;
        // Risk Management
        config.MinHoldBeforePnlTargetMinutes = candidate.MinHoldBeforePnlTargetMinutes;
        config.LiquidationWarningPct = candidate.LiquidationWarningPct;
        config.LiquidationEarlyWarningPct = candidate.LiquidationEarlyWarningPct;
        config.ExchangeCircuitBreakerThreshold = candidate.ExchangeCircuitBreakerThreshold;
        config.ExchangeCircuitBreakerMinutes = candidate.ExchangeCircuitBreakerMinutes;
        config.PriceFeedFailureCloseThreshold = candidate.PriceFeedFailureCloseThreshold;
        // Thresholds
        config.EmergencyCloseSpreadThreshold = candidate.EmergencyCloseSpreadThreshold;
        config.MinEdgeMultiplier = candidate.MinEdgeMultiplier;
        config.DivergenceAlertMultiplier = candidate.DivergenceAlertMultiplier;
        config.DivergenceAlertConfirmationCycles = candidate.DivergenceAlertConfirmationCycles;
        config.RotationDivergenceHorizonHours = candidate.RotationDivergenceHorizonHours;
        config.PreferCloseOnDivergenceNarrowing = candidate.PreferCloseOnDivergenceNarrowing;
        config.SlippageBufferBps = candidate.SlippageBufferBps;
        config.StablecoinAlertThresholdPct = candidate.StablecoinAlertThresholdPct;
        config.StablecoinCriticalThresholdPct = candidate.StablecoinCriticalThresholdPct;
        config.MinConsecutiveFavorableCycles = candidate.MinConsecutiveFavorableCycles;
        config.FundingFlipExitCycles = candidate.FundingFlipExitCycles;
        // Advanced booleans
        config.UseRiskBasedDivergenceClose = candidate.UseRiskBasedDivergenceClose;
        config.UseBreakEvenSizeFilter = candidate.UseBreakEvenSizeFilter;
        config.PairAutoDenyEnabled = candidate.PairAutoDenyEnabled;
        config.DryRunEnabled = candidate.DryRunEnabled;
        config.ForceConcurrentExecution = candidate.ForceConcurrentExecution;
        // Infrastructure
        config.ReconciliationIntervalCycles = candidate.ReconciliationIntervalCycles;

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
