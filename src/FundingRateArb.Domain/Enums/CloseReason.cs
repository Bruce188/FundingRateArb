namespace FundingRateArb.Domain.Enums;

public enum CloseReason
{
    SpreadCollapsed,
    MaxHoldTimeReached,
    StopLoss,
    Manual,
    EmergencyLegFailed
}
