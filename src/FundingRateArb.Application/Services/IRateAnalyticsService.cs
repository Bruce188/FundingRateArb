using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface IRateAnalyticsService
{
    Task<List<RateTrendDto>> GetRateTrendsAsync(int? assetId, int days = 7, int? exchangeId = null, CancellationToken ct = default);
    Task<List<CorrelationPairDto>> GetCrossExchangeCorrelationAsync(int assetId, int days = 7, CancellationToken ct = default);
    Task<List<TimeOfDayPatternDto>> GetTimeOfDayPatternsAsync(int assetId, int exchangeId, int days = 7, CancellationToken ct = default);
    Task<List<ZScoreAlertDto>> GetZScoreAlertsAsync(decimal threshold = 2.0m, CancellationToken ct = default);
}
