namespace FundingRateArb.Application.DTOs;

public record RebalanceRecommendationDto(
    int PositionId,
    string PositionAsset,
    decimal CurrentSpread,
    decimal RemainingExpectedPnl,
    string ReplacementAsset,
    string ReplacementLongExchange,
    string ReplacementShortExchange,
    decimal ReplacementSpread,
    decimal ExpectedImprovement);
