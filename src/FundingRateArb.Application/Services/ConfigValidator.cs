using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public class ConfigValidator : IConfigValidator
{
    // Taker fee on both legs, both sides (open + close, long + short)
    private const decimal RoundTripFeeRate = 0.001m;

    public ConfigValidationResult Validate(BotConfiguration config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (config.OpenThreshold <= config.AlertThreshold)
        {
            errors.Add("OpenThreshold must be greater than AlertThreshold.");
        }

        if (config.AlertThreshold <= 0)
        {
            errors.Add("AlertThreshold must be positive.");
        }

        if (config.CloseThreshold < -0.001m)
        {
            errors.Add("CloseThreshold must be >= -0.001 (small negative allows slight bleed before closing; below -0.001 is excessive).");
        }

        if (config.FeeAmortizationHours < 1)
        {
            errors.Add("FeeAmortizationHours must be at least 1.");
        }

        if (config.RateStalenessMinutes < 1)
        {
            errors.Add("RateStalenessMinutes must be at least 1.");
        }

        if (config.FeeAmortizationHours > config.MaxHoldTimeHours)
        {
            errors.Add("FeeAmortizationHours must not exceed MaxHoldTimeHours.");
        }

        if (config.DefaultLeverage > 10)
        {
            errors.Add("Leverage above 10x is not recommended for funding rate arbitrage.");
        }

        if (config.AllocationStrategy != AllocationStrategy.Concentrated
            && config.MaxConcurrentPositions < config.AllocationTopN)
        {
            errors.Add("MaxConcurrentPositions must be >= AllocationTopN for non-Concentrated strategies.");
        }

        if (config.MinPositionSizeUsdc <= 0)
        {
            errors.Add("MinPositionSizeUsdc must be positive.");
        }

        if (config.DailyDrawdownPausePct <= 0 || config.DailyDrawdownPausePct > 1)
        {
            errors.Add("DailyDrawdownPausePct must be between 0 and 1 (exclusive).");
        }

        if (config.ConsecutiveLossPause < 1)
        {
            errors.Add("ConsecutiveLossPauseCount must be at least 1.");
        }

        if (config.CloseThreshold >= config.AlertThreshold)
        {
            errors.Add("CloseThreshold must be less than AlertThreshold.");
        }

        if (config.DefaultLeverage < 1)
        {
            errors.Add("DefaultLeverage must be at least 1.");
        }

        if (config.MaxHoldTimeHours < 1)
        {
            errors.Add("MaxHoldTimeHours must be at least 1 hour.");
        }

        if (config.MinHoldTimeHours > config.MaxHoldTimeHours)
        {
            errors.Add("MinHoldTimeHours must not exceed MaxHoldTimeHours.");
        }

        if (config.MaxExposurePerAsset <= 0 || config.MaxExposurePerAsset > 1)
        {
            errors.Add("MaxExposurePerAsset must be between 0 (exclusive) and 1 (inclusive).");
        }

        if (config.MaxExposurePerExchange <= 0 || config.MaxExposurePerExchange > 1)
        {
            errors.Add("MaxExposurePerExchange must be between 0 (exclusive) and 1 (inclusive).");
        }

        if (config.AllocationStrategy != AllocationStrategy.Concentrated
            && config.MaxCapitalPerPosition * config.MaxConcurrentPositions > 1.5m)
        {
            errors.Add("MaxCapitalPerPosition × MaxConcurrentPositions exceeds 150% — risk of capital over-allocation.");
        }

        // Gap invariant: OpenThreshold must cover fees over the minimum hold period.
        // When MinHoldTimeHours == 0 the check is suppressed (divide-by-zero is undefined
        // and existing validation already rejects MinHoldTimeHours > MaxHoldTimeHours; a
        // zero value indicates a misconfigured entity and we avoid a secondary exception).
        if (config.MinHoldTimeHours > 0)
        {
            var minSpread = config.CloseThreshold + RoundTripFeeRate / config.MinHoldTimeHours;
            if (config.OpenThreshold < minSpread)
            {
                warnings.Add(
                    $"OpenThreshold ({config.OpenThreshold}) is below CloseThreshold + round-trip fee / MinHoldTimeHours " +
                    $"({minSpread:G}). Trades may not cover fees over the minimum hold period.");
            }
        }

        return new ConfigValidationResult(errors.Count == 0, errors, warnings.Count > 0 ? warnings : null);
    }
}
