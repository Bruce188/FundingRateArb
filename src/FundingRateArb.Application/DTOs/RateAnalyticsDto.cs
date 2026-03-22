namespace FundingRateArb.Application.DTOs;

public record RateTrendDto(
    int AssetId,
    string AssetSymbol,
    int ExchangeId,
    string ExchangeName,
    decimal CurrentRate,
    decimal Avg7d,
    decimal Avg24h,
    string TrendDirection,
    List<HourlyRatePoint> HourlyRates);

public record HourlyRatePoint(
    DateTime HourUtc,
    decimal AvgRate,
    decimal MinRate,
    decimal MaxRate,
    decimal Volume);

public record CorrelationPairDto(
    string Exchange1,
    string Exchange2,
    decimal PearsonR,
    int SampleCount);

public record TimeOfDayPatternDto(
    int HourUtc,
    decimal AvgRate,
    decimal StdDev,
    int SampleCount);

public record ZScoreAlertDto(
    string AssetSymbol,
    string ExchangeName,
    decimal CurrentRate,
    decimal Mean7d,
    decimal StdDev7d,
    decimal ZScore);
