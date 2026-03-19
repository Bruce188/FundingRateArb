using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public class YieldCalculator : IYieldCalculator
{
    /// <summary>
    /// Converts an hourly funding rate to an annualised yield.
    /// Formula: ratePerHour * 24 * 365
    /// Example: 0.0004/hr → 3.504 (350.4% APY)
    /// </summary>
    public decimal AnnualizedYield(decimal ratePerHour)
        => ratePerHour * 24m * 365m;

    /// <summary>
    /// Projects gross PnL over a given number of hours.
    /// Formula: sizeUsdc * netRatePerHour * hours
    /// Example: $1 000 * 0.0004 * 24 = $9.60
    /// </summary>
    public decimal ProjectedPnl(decimal sizeUsdc, decimal netRatePerHour, decimal hours)
        => sizeUsdc * netRatePerHour * hours;

    /// <summary>
    /// Returns the current unrealised PnL for an open position.
    /// Uses AccumulatedFunding when the exchange has settled at least one payment;
    /// otherwise falls back to a linear estimate from the entry spread.
    /// </summary>
    public decimal UnrealizedPnl(ArbitragePosition pos)
    {
        if (pos.AccumulatedFunding != 0m)
            return pos.AccumulatedFunding;

        var hoursOpen = (decimal)(DateTime.UtcNow - pos.OpenedAt).TotalHours;
        return pos.SizeUsdc * pos.EntrySpreadPerHour * hoursOpen;
    }

    /// <summary>
    /// Calculates how many hours of funding are needed to cover entry fees.
    /// Formula: feeRateTotal / netRatePerHour
    /// Returns decimal.MaxValue when the net rate is zero or negative
    /// (position would never break even).
    /// Example: 0.09% fees / 0.04%/hr = 2.25 hours
    /// </summary>
    public decimal BreakEvenHours(decimal feeRateTotal, decimal netRatePerHour)
    {
        if (netRatePerHour <= 0m)
            return decimal.MaxValue;

        return feeRateTotal / netRatePerHour;
    }
}
