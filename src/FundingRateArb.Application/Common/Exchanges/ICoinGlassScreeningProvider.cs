namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Screening layer that fetches pre-calculated cross-exchange arbitrage opportunities
/// from the CoinGlass /api/futures/funding-rate/arbitrage endpoint. Used by SignalEngine
/// as a fast first-pass hint for prioritizing symbols known to have active opportunities
/// across the broader exchange universe tracked by CoinGlass.
/// </summary>
public interface ICoinGlassScreeningProvider
{
    /// <summary>
    /// Returns the set of normalized symbol identifiers that are currently showing
    /// arbitrage opportunities at or above the configured APR threshold on CoinGlass.
    /// The set is used as a priority hint in opportunity scoring — matching symbols
    /// should be preferred when equivalent opportunities exist.
    /// Returns an empty set on API failure or when the feature is disabled.
    /// </summary>
    Task<IReadOnlySet<string>> GetHotSymbolsAsync(CancellationToken ct = default);
}
