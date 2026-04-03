using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IRotationEvaluator
{
    RotationRecommendationDto? Evaluate(
        IReadOnlyList<ArbitragePosition> openPositions,
        IReadOnlyList<ArbitrageOpportunityDto> opportunities,
        UserConfiguration userConfig,
        BotConfiguration globalConfig);
}
