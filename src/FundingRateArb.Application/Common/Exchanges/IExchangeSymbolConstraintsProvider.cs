namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Resolves per-exchange / per-symbol notional caps so <c>SignalEngine</c> can
/// filter candidate opportunities that would exceed an exchange's
/// <c>MAX_NOTIONAL_VALUE</c> limit before an order is placed.
///
/// Returning <c>null</c> means the exchange either does not expose a cap or the
/// cap could not be fetched — the caller should treat null as "no cap known"
/// and let the candidate pass.
/// </summary>
public interface IExchangeSymbolConstraintsProvider
{
    Task<decimal?> GetMaxNotionalAsync(string exchangeName, string symbol, CancellationToken ct = default);
}
