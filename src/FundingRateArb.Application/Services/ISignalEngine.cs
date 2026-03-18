using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface ISignalEngine
{
    Task<List<ArbitrageOpportunityDto>> GetOpportunitiesAsync();
}
