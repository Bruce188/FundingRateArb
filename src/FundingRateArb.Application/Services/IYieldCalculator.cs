using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IYieldCalculator
{
    decimal AnnualizedYield(decimal ratePerHour);
    decimal ProjectedPnl(decimal sizeUsdc, decimal netRatePerHour, decimal hours);
    decimal UnrealizedPnl(ArbitragePosition pos);
    decimal BreakEvenHours(decimal feeRateTotal, decimal netRatePerHour);
}
