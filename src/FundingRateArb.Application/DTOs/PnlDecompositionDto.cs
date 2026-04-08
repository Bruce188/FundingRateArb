namespace FundingRateArb.Application.DTOs;

/// <summary>
/// Three-component PnL decomposition per Analysis Section 4.7:
/// Strategy PnL = directional + funding - fees.
/// </summary>
/// <remarks>
/// The three components are tracked separately so that per-exchange differences in how
/// realized PnL is bundled (Lighter bundles funding into realized_pnl; Binance/Aster report
/// FUNDING_FEE and COMMISSION as separate income types; Hyperliquid tracks cumFunding alongside
/// unrealizedPnl; dYdX adjusts quoteBalance via funding history) do not contaminate the
/// bot's strategy-level accounting. Strategy PnL must always be assembled from the bot's own
/// tracking (actual fill prices, locally accumulated funding, locally recorded fees) rather
/// than any single exchange's reported PnL field.
/// </remarks>
public sealed record PnlDecompositionDto(
    decimal Directional,
    decimal Funding,
    decimal Fees)
{
    /// <summary>Strategy PnL = directional + funding - fees.</summary>
    public decimal Strategy => Directional + Funding - Fees;
}
