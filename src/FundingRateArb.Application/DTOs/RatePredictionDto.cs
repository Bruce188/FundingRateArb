namespace FundingRateArb.Application.DTOs;

public record RatePredictionDto(
    int AssetId,
    string AssetSymbol,
    int ExchangeId,
    string ExchangeName,
    decimal PredictedRatePerHour,
    decimal Confidence,
    string TrendDirection);
