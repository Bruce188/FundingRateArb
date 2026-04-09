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

    /// <summary>
    /// Indicates whether the screening provider is currently reachable. Flips to
    /// <c>false</c> when the underlying Polly circuit breaker opens (after consecutive
    /// failures) and back to <c>true</c> on the first successful call once the
    /// breaker half-opens. SignalEngine reads this flag after <see cref="GetHotSymbolsAsync"/>
    /// to emit a once-per-cycle "skipped (unavailable)" warning instead of a per-candidate log.
    /// </summary>
    bool IsAvailable { get; }
}
