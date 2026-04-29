using System.Text.Json.Serialization;

namespace FundingRateArb.Infrastructure.ExchangeConnectors.Models;

/// <summary>
/// Single price/size level from Lighter's <c>/api/v1/orderBook</c> response.
/// </summary>
public class LighterOrderBookLevel
{
    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("size")]
    public decimal Size { get; set; }
}

/// <summary>
/// Multi-level orderbook response from Lighter's <c>/api/v1/orderBook?market_id={id}</c> endpoint.
/// Used by <c>LighterConnector.GetOrderbookDepthAsync</c> as the REST fallback when the WS book
/// cache is stale or empty.
/// </summary>
public class LighterOrderBookResponse
{
    [JsonPropertyName("market_id")]
    public int MarketId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("bids")]
    public List<LighterOrderBookLevel> Bids { get; set; } = new();

    [JsonPropertyName("asks")]
    public List<LighterOrderBookLevel> Asks { get; set; } = new();
}
