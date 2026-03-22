using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IPortfolioRebalancer
{
    Task<List<RebalanceRecommendationDto>> EvaluateAsync(
        IReadOnlyList<ArbitragePosition> openPositions,
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        BotConfiguration config,
        CancellationToken ct = default);
}
