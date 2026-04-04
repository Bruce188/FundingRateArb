namespace FundingRateArb.Domain.ValueObjects;

public record LeverageTier(
    decimal NotionalFloor,
    decimal NotionalCap,
    int MaxLeverage,
    decimal MaintMarginRate);
