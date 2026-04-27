namespace FundingRateArb.Application.Services;

/// <summary>
/// Computes the minimum-viable OpenThreshold required to ensure positions accrue
/// enough funding to outpace round-trip fees within MinHoldTimeHours. Invariant:
/// OpenThreshold ≥ |CloseThreshold| + (EstimatedRoundTripFeeRate / MinHoldTimeHours).
/// </summary>
public static class ThresholdInvariantCalculator
{
    /// <summary>
    /// Estimated round-trip fee rate (taker fees on both legs, both open and close).
    /// First-cut hardcoded at 0.001 (10 bps). Future work: per-exchange-fee abstraction.
    /// </summary>
    public const decimal EstimatedRoundTripFeeRate = 0.001m;

    /// <summary>
    /// Computes the minimum-viable OpenThreshold given the close threshold and minimum hold time.
    /// Returns decimal.MaxValue when minHoldTimeHours is non-positive (defensive fail-closed).
    /// </summary>
    public static decimal ComputeRequiredOpenFloor(decimal closeThreshold, int minHoldTimeHours)
    {
        if (minHoldTimeHours <= 0)
        {
            return decimal.MaxValue;
        }

        return Math.Abs(closeThreshold) + EstimatedRoundTripFeeRate / minHoldTimeHours;
    }

    /// <summary>
    /// True when openThreshold is below the computed required floor.
    /// </summary>
    public static bool IsViolated(decimal openThreshold, decimal closeThreshold, int minHoldTimeHours)
        => openThreshold < ComputeRequiredOpenFloor(closeThreshold, minHoldTimeHours);
}
