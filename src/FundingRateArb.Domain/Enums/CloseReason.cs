namespace FundingRateArb.Domain.Enums;

public enum CloseReason
{
    SpreadCollapsed = 0,
    MaxHoldTimeReached = 1,
    StopLoss = 2,
    PnlTargetReached = 3,
    Rebalanced = 4,
    Manual = 5,
    EmergencyLegFailed = 6,
    PriceFeedLost = 7,
    ExchangeDrift = 8,
    LiquidationRisk = 9,
    Rotation = 10,
    FundingFlipped = 11,
    StablecoinDepeg = 12,
    DivergenceCritical = 13,
    ReconciliationDrift = 14,
    None = 99,
}
