using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Services;

public class PositionSizer : IPositionSizer
{
    private readonly IUnitOfWork _uow;
    private readonly IYieldCalculator _yieldCalculator;

    public PositionSizer(IUnitOfWork uow, IYieldCalculator yieldCalculator)
    {
        _uow = uow;
        _yieldCalculator = yieldCalculator;
    }

    public async Task<decimal> CalculateOptimalSizeAsync(ArbitrageOpportunityDto opp)
    {
        if (opp.NetYieldPerHour <= 0)
            return 0m;

        var config = await _uow.BotConfig.GetActiveAsync();

        // Break-even limit: fee rate = gross spread minus net yield (fees subtracted by SignalEngine)
        var entryFeeRate = opp.SpreadPerHour - opp.NetYieldPerHour;
        // H4: Negative entryFeeRate means spread < net yield — invalid opportunity, reject
        if (entryFeeRate < 0)
            return 0m;
        var breakEvenHours = _yieldCalculator.BreakEvenHours(entryFeeRate, opp.NetYieldPerHour);
        if (breakEvenHours > config.BreakevenHoursMax)
            return 0m;

        var capitalLimit = config.TotalCapitalUsdc
                           * config.MaxCapitalPerPosition
                           * config.DefaultLeverage;

        var minVolume = Math.Min(opp.LongVolume24h, opp.ShortVolume24h);
        var liquidityLimit = minVolume * config.VolumeFraction;

        return Math.Min(capitalLimit, liquidityLimit);
    }

    public async Task<int> CalculateMaxPositionsAsync(decimal sizePerPosition)
    {
        if (sizePerPosition <= 0)
            return 0;

        var config = await _uow.BotConfig.GetActiveAsync();
        var rawMax = (int)(config.TotalCapitalUsdc / sizePerPosition);
        return Math.Min(rawMax, config.MaxConcurrentPositions);
    }

    public static decimal RoundToStepSize(decimal quantity, decimal stepSize, int decimals)
    {
        if (stepSize <= 0) return Math.Round(quantity, decimals);
        var steps = Math.Floor(quantity / stepSize);
        var rounded = steps * stepSize;
        return Math.Round(rounded, decimals);
    }
}
