namespace FundingRateArb.Application.Common;

/// <summary>
/// Single source of truth for per-exchange taker fee rates.
/// Values are one-way rates. Multiply by 2 for round-trip.
/// </summary>
public static class ExchangeFeeConstants
{
    public static decimal GetTakerFeeRate(string? exchangeName) => exchangeName switch
    {
        "Hyperliquid" => 0.00045m,
        "Lighter" => 0m,
        "Aster" => 0.0004m,
        _ => 0.0005m,
    };
}
