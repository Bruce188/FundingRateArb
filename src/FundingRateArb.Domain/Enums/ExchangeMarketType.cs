namespace FundingRateArb.Domain.Enums;

/// <summary>
/// Hardcoded per-connector capability declaring whether it speaks perp futures or spot.
/// Stored as a code property on each <c>IExchangeConnector</c> implementation; never
/// configured at runtime via DB or appsettings (analysis Constraint).
/// </summary>
public enum ExchangeMarketType
{
    /// <summary>Perpetual futures (default — all existing connectors).</summary>
    Perp = 0,

    /// <summary>Spot — no leverage, no funding, cannot be the short leg.</summary>
    Spot = 1,
}
