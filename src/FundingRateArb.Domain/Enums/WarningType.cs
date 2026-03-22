namespace FundingRateArb.Domain.Enums;

/// <summary>
/// Type of warning condition detected on a position.
/// Multiple types can be active simultaneously; the highest WarningLevel across all types is used.
/// </summary>
public enum WarningType
{
    SpreadRisk,
    Liquidity,
    TimeBased,
    Leverage,
    Loss
}
