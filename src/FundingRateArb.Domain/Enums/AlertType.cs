namespace FundingRateArb.Domain.Enums;

public enum AlertType
{
    OpportunityDetected,
    SpreadWarning,
    SpreadCollapsed,
    PositionOpened,
    PositionClosed,
    LegFailed,
    BotError,
    MarginWarning,
    PriceFeedFailure,
    LeverageReduced,
    QuantityMismatch,
    PnlDivergence
}
