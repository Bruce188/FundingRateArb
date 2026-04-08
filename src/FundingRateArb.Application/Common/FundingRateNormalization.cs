namespace FundingRateArb.Application.Common;

/// <summary>
/// Centralized helpers for funding rate interval normalization per Analysis Section 7.1
/// and Appendix B Normalized Rate Comparison.
/// </summary>
/// <remarks>
/// Different exchanges settle funding on different intervals (Hyperliquid/Lighter/dYdX =
/// hourly, Aster/Binance = 8-hourly default but variable). Cross-exchange opportunity
/// comparison must normalize rates to a common basis; using a raw 8-hourly rate against a
/// raw hourly rate would undervalue the hourly side by 8×.
/// Critically, normalization is used ONLY for comparison. Per-exchange PnL tracking must
/// still use the native interval so accumulated funding matches exchange-reported amounts.
/// </remarks>
public static class FundingRateNormalization
{
    /// <summary>The reference interval for cross-exchange rate comparison.</summary>
    public const int ReferenceIntervalHours = 8;

    /// <summary>Hours in a standard year for APR calculations.</summary>
    public const int HoursPerYear = 24 * 365;

    /// <summary>
    /// Converts a per-hour funding rate to the equivalent 8-hour rate.
    /// Use this only when comparing rates across exchanges with different native intervals —
    /// never for PnL accounting.
    /// </summary>
    public static decimal ToEightHourRate(decimal ratePerHour)
    {
        return ratePerHour * ReferenceIntervalHours;
    }

    /// <summary>
    /// Converts a raw per-interval funding rate to per-hour given the exchange's settlement
    /// interval. Intended for use inside connectors that receive rates in their native
    /// format (e.g., Binance returns an 8h rate that must be divided by 8 before comparison).
    /// </summary>
    /// <param name="rawRate">The exchange-reported rate for one funding interval.</param>
    /// <param name="intervalHours">The exchange's funding interval in hours.</param>
    public static decimal ToPerHourRate(decimal rawRate, int intervalHours)
    {
        if (intervalHours <= 0)
        {
            return rawRate;
        }

        return rawRate / intervalHours;
    }

    /// <summary>
    /// Annualizes a per-hour funding rate to APR (fraction, not percent).
    /// A rate of 0.0001/hour = 0.0001 × 8760 ≈ 0.876 APR.
    /// </summary>
    public static decimal ToAnnualizedRate(decimal ratePerHour)
    {
        return ratePerHour * HoursPerYear;
    }

    /// <summary>
    /// Number of funding cycles per year for a given interval. Used in return-on-capital
    /// projections that need to know how many payment events occur annually.
    /// </summary>
    public static decimal CyclesPerYear(int intervalHours)
    {
        if (intervalHours <= 0)
        {
            return 0m;
        }

        return (decimal)HoursPerYear / intervalHours;
    }
}
