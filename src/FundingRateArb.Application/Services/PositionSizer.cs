using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public class PositionSizer : IPositionSizer
{
    private readonly IUnitOfWork _uow;

    public PositionSizer(IUnitOfWork uow) => _uow = uow;

    public Task<decimal> CalculateOptimalSizeAsync(ArbitrageOpportunityDto opp)
        => throw new NotImplementedException();

    public Task<int> CalculateMaxPositionsAsync(decimal sizePerPosition)
        => throw new NotImplementedException();

    public static decimal RoundToStepSize(decimal quantity, decimal stepSize, int decimals)
    {
        if (stepSize <= 0) return Math.Round(quantity, decimals);
        var steps = Math.Floor(quantity / stepSize);
        var rounded = steps * stepSize;
        return Math.Round(rounded, decimals);
    }
}
