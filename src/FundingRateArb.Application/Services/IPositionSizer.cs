using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IPositionSizer
{
    Task<decimal> CalculateOptimalSizeAsync(ArbitrageOpportunityDto opp);
    Task<decimal[]> CalculateBatchSizesAsync(
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        AllocationStrategy strategy);
}
