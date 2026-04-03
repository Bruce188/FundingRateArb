namespace FundingRateArb.Application.DTOs;

public record RotationRecommendationDto(
    int PositionId,
    string PositionAsset,
    decimal CurrentSpreadPerHour,
    int ReplacementAssetId,
    string ReplacementAsset,
    string ReplacementLongExchange,
    string ReplacementShortExchange,
    int ReplacementLongExchangeId,
    int ReplacementShortExchangeId,
    decimal ReplacementNetYieldPerHour,
    decimal ImprovementPerHour);
