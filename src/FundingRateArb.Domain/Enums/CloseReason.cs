namespace FundingRateArb.Domain.Enums;

public enum CloseReason
{
    SpreadCollapsed,
    MaxHoldTimeReached,
    StopLoss,
    PnlTargetReached,
    Rebalanced,
    Manual,
    EmergencyLegFailed,
    PriceFeedLost
}
