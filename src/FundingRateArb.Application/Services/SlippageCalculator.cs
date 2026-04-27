namespace FundingRateArb.Application.Services;

/// <summary>
/// Computes per-leg slippage as a fraction: (fillPrice - intendedMid) / intendedMid.
/// Returns null when either input is missing, zero, or negative — matching the
/// AC1 "null when either is missing or unknown" semantics required for the new
/// LongEntrySlippagePct / ShortEntrySlippagePct / LongExitSlippagePct / ShortExitSlippagePct
/// columns on ArbitragePosition.
/// </summary>
public static class SlippageCalculator
{
    public static decimal? Compute(decimal? intendedMid, decimal? fillPrice)
    {
        if (intendedMid is null || fillPrice is null) return null;
        if (intendedMid.Value <= 0m || fillPrice.Value <= 0m) return null;
        return (fillPrice.Value - intendedMid.Value) / intendedMid.Value;
    }

    /// <summary>
    /// True when |slippagePct| exceeds threshold. Threshold is interpreted as a magnitude
    /// (positive value); slippage may be positive or negative depending on fill direction.
    /// Returns false when slippagePct is null.
    /// </summary>
    public static bool ExceedsThreshold(decimal? slippagePct, decimal threshold)
    {
        if (slippagePct is null) return false;
        return Math.Abs(slippagePct.Value) > threshold;
    }
}
