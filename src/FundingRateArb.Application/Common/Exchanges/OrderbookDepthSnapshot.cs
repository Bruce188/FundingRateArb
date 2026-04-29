using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Source of the depth snapshot — used by callers to decide whether to log a fallback warning.
/// </summary>
public enum OrderbookDepthSource
{
    WsCache = 0,
    RestFallback = 1,

    /// <summary>The ladder did not contain enough depth for the requested quantity.
    /// Caller (the depth gate) treats this as REJECT.</summary>
    Insufficient = 2,
}

/// <summary>
/// Result of <c>IExchangeConnector.GetOrderbookDepthAsync</c>: an estimated avg-fill price for
/// the requested <c>(asset, side, quantity)</c>, derived by walking the order ladder.
/// </summary>
/// <param name="Asset">Asset symbol (matches the connector's <c>ExchangeName</c>-normalised form).</param>
/// <param name="Side">Side that was queried — <c>Long</c> = buy base = walk asks ascending,
/// <c>Short</c> = sell base = walk bids descending.</param>
/// <param name="IntendedMidPrice">Intended mid <c>(BestBid + BestAsk) / 2</c> at snapshot time.</param>
/// <param name="EstimatedAvgFillPrice">Size-weighted average fill price across walked levels.
/// Zero when <paramref name="Source"/> is <c>Insufficient</c>.</param>
/// <param name="EstimatedSlippagePct">Estimated slippage as a fraction:
/// <c>(EstimatedAvgFillPrice - IntendedMidPrice) / IntendedMidPrice</c>. Zero when
/// <paramref name="Source"/> is <c>Insufficient</c>.</param>
/// <param name="Source">Where the snapshot was sourced from.</param>
public record OrderbookDepthSnapshot(
    string Asset,
    Side Side,
    decimal IntendedMidPrice,
    decimal EstimatedAvgFillPrice,
    decimal EstimatedSlippagePct,
    OrderbookDepthSource Source);
