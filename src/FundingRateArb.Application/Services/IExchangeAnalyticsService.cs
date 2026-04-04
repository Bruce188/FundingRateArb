using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface IExchangeAnalyticsService
{
    Task<List<ExchangeOverviewDto>> GetExchangeOverviewAsync(CancellationToken ct = default);
    Task<List<SpreadOpportunityDto>> GetTopOpportunitiesAsync(int count = 20, decimal minSpreadPerHour = 0.00005m, CancellationToken ct = default);
    Task<List<RateComparisonDto>> GetRateComparisonsAsync(CancellationToken ct = default);
    Task<List<DiscoveryEventDto>> GetRecentDiscoveryEventsAsync(int days = 7, CancellationToken ct = default);
}
