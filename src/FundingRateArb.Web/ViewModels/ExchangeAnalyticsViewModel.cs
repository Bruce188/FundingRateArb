using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class ExchangeAnalyticsViewModel
{
    public List<ExchangeOverviewDto> Exchanges { get; set; } = [];
    public List<SpreadOpportunityDto> TopOpportunities { get; set; } = [];
    public List<RateComparisonDto> RateComparisons { get; set; } = [];
    public List<DiscoveryEventDto> DiscoveryEvents { get; set; } = [];
}
