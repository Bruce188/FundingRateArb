using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class ConfigValidator : IConfigValidator
{
    public ConfigValidationResult Validate(BotConfiguration config)
    {
        var errors = new List<string>();

        if (config.OpenThreshold <= config.AlertThreshold)
            errors.Add("OpenThreshold must be greater than AlertThreshold.");

        if (config.AlertThreshold <= 0)
            errors.Add("AlertThreshold must be positive.");

        if (config.CloseThreshold < 0)
            errors.Add("CloseThreshold must be >= 0 (negative means position bleeds money before closing).");

        if (config.FeeAmortizationHours < 1)
            errors.Add("FeeAmortizationHours must be at least 1.");

        if (config.RateStalenessMinutes < 1)
            errors.Add("RateStalenessMinutes must be at least 1.");

        if (config.FeeAmortizationHours > config.MaxHoldTimeHours)
            errors.Add("FeeAmortizationHours must not exceed MaxHoldTimeHours.");

        if (config.DefaultLeverage > 10)
            errors.Add("Leverage above 10x is not recommended for funding rate arbitrage.");

        if (config.AllocationStrategy != AllocationStrategy.Concentrated
            && config.MaxConcurrentPositions < config.AllocationTopN)
            errors.Add("MaxConcurrentPositions must be >= AllocationTopN for non-Concentrated strategies.");

        if (config.MinPositionSizeUsdc <= 0)
            errors.Add("MinPositionSizeUsdc must be positive.");

        if (config.DailyDrawdownPausePct <= 0 || config.DailyDrawdownPausePct > 1)
            errors.Add("DailyDrawdownPausePct must be between 0 and 1 (exclusive).");

        return new ConfigValidationResult(errors.Count == 0, errors);
    }
}
