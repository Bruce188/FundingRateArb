namespace FundingRateArb.Application.Services;

/// <summary>
/// Groups skip-reason tracking sets to reduce parameter count.
/// Per-user sets are cleared at the start of each user iteration to prevent cross-user leakage.
/// Global sets (CapitalExhaustedKeys, MaxPositionsKeys, CooldownKeys) accumulate across all users
/// and represent "any-user" aggregate status.
/// </summary>
public sealed class SkipReasonTracker
{
    // Global sets (accumulated across all users — represents "any-user" aggregate status)
    public HashSet<string> OpenedOppKeys { get; } = new();
    public HashSet<string> CapitalExhaustedKeys { get; } = new();
    public HashSet<string> MaxPositionsKeys { get; } = new();
    public HashSet<string> CooldownKeys { get; } = new();

    // Per-user sets (cleared before each user iteration)
    public HashSet<string> ExchangeDisabledKeys { get; } = new();
    public HashSet<string> AssetDisabledKeys { get; } = new();
    public HashSet<string> CircuitBrokenKeys { get; } = new();
    public HashSet<string> NotSelectedKeys { get; } = new();
    public HashSet<string> FundingDeviationWindowKeys { get; } = new();

    // Per-user below-threshold tracking (cleared before each user iteration)
    public int BelowThresholdCount { get; set; }
    public decimal BestBelowThresholdYield { get; set; } = decimal.MinValue;

    /// <summary>Clears per-user skip reason sets before processing a new user.</summary>
    public void ClearPerUserSets()
    {
        ExchangeDisabledKeys.Clear();
        AssetDisabledKeys.Clear();
        CircuitBrokenKeys.Clear();
        NotSelectedKeys.Clear();
        FundingDeviationWindowKeys.Clear();
        BelowThresholdCount = 0;
        BestBelowThresholdYield = decimal.MinValue;
    }
}
