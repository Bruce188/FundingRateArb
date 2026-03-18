using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public interface IPositionSizer
{
    Task<decimal> CalculateOptimalSizeAsync(ArbitrageOpportunityDto opp);
    Task<int> CalculateMaxPositionsAsync(decimal sizePerPosition);
    static decimal RoundToStepSize(decimal quantity, decimal stepSize, int decimals)
        => throw new NotImplementedException();
}
