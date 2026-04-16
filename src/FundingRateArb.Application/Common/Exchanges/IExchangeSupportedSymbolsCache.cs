namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Process-scoped cache that holds the set of tradeable perpetual symbols for each
/// exchange. Used by <c>MarketDataStreamManager</c> to filter symbols before subscribing
/// so that exchanges never receive subscribe requests for symbols they don't support.
/// </summary>
public interface IExchangeSupportedSymbolsCache
{
    /// <summary>
    /// Returns the set of symbols supported by <paramref name="exchangeName"/>.
    /// On the first call (or after TTL expiry) the metadata endpoint is queried.
    /// On upstream failure the last-known-good set is returned; empty set if never loaded.
    /// </summary>
    Task<HashSet<string>> GetSupportedSymbolsAsync(string exchangeName, CancellationToken ct = default);

    /// <summary>
    /// Force-refreshes the symbol set for all known exchanges.
    /// Useful for health-check or admin-triggered refresh flows.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}
