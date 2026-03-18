using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public class YieldCalculator : IYieldCalculator
{
    public decimal AnnualizedYield(decimal ratePerHour)
        => throw new NotImplementedException();

    public decimal ProjectedPnl(decimal sizeUsdc, decimal netRatePerHour, decimal hours)
        => throw new NotImplementedException();

    public decimal UnrealizedPnl(ArbitragePosition pos)
        => throw new NotImplementedException();

    public decimal BreakEvenHours(decimal sizeUsdc, decimal feeRateTotal, decimal netRatePerHour)
        => throw new NotImplementedException();
}
