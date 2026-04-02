using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Services;

public interface IPositionSizer
{
    Task<decimal[]> CalculateBatchSizesAsync(
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        AllocationStrategy strategy,
        string userId,
        UserConfiguration? userConfig = null,
        CancellationToken ct = default);
}
