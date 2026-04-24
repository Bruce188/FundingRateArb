using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IYieldCalculator
{
    decimal AnnualizedYield(decimal ratePerHour);
    decimal ProjectedPnl(decimal sizeUsdc, decimal netRatePerHour, decimal hours);
    decimal UnrealizedPnl(ArbitragePosition pos);
    decimal BreakEvenHours(decimal feeRateTotal, decimal netRatePerHour);

    /// <summary>
    /// Calculates the total funding accrued over a window using RawRate-based arithmetic.
    /// Formula: rawRate * (windowHours / nativeIntervalHours)
    /// Uses RawRate per Appendix B — per-hour projection is display-only.
    /// </summary>
    decimal AccruedFunding(decimal rawRate, int nativeIntervalHours, decimal windowHours);
}
