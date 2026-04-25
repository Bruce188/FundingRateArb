namespace FundingRateArb.Domain.Enums;

public enum CloseReason
{
    None = 0,
    SpreadCollapsed,
    MaxHoldTimeReached,
    StopLoss,
    PnlTargetReached,
    Rebalanced,
    Manual,
    EmergencyLegFailed,
    PriceFeedLost,
    ExchangeDrift,
    LiquidationRisk,
    Rotation,
    FundingFlipped,
    StablecoinDepeg,
    DivergenceCritical,
    ReconciliationDrift
}
