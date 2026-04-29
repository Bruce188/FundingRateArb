using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors.Models;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class LighterMarketDataStream : IMarketDataStream
{
    private readonly LighterWebSocketClient _wsClient;
    private readonly IMarketDataCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LighterMarketDataStream> _logger;
    private readonly Dictionary<int, string> _marketIndexToSymbol = new();

    // In-memory WS state used by LighterConnector.HasAnyLiquidity for the empty-book fallback.
    private readonly Dictionary<string, (decimal Price, DateTime ReceivedAt)> _wsLastTradePrice = new();
    private readonly Dictionary<string, (decimal Bid, decimal Ask)> _wsBookLevels = new();

    // Multi-level WS book ladder for GetOrderbookDepthAsync (depth-gate). Populated when the WS
    // payload includes multi-level bid/ask arrays. Bids descending, asks ascending.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (List<(decimal Price, decimal Size)> Bids, List<(decimal Price, decimal Size)> Asks, DateTime ReceivedAt)
    > _wsBookLadder = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public LighterMarketDataStream(
        LighterWebSocketClient wsClient,
        IMarketDataCache cache,
        IHttpClientFactory httpClientFactory,
        ILogger<LighterMarketDataStream> logger)
    {
        _wsClient = wsClient;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ExchangeName => "Lighter";
    public bool IsConnected => _wsClient.IsConnected;
    public event Action<FundingRateDto>? OnRateUpdate;
    public event Action<string, string>? OnDisconnected;

    /// <summary>Seed market-index-to-symbol mappings for unit testing.</summary>
    internal void SetMarketMapping(int marketIndex, string symbol)
        => _marketIndexToSymbol[marketIndex] = symbol;

    // ── WS liveness accessors (used by LighterConnector.HasAnyLiquidity) ──

    /// <summary>
    /// Returns the last WS-received trade price and its receive time for <paramref name="symbol"/>,
    /// or null if no data has been received for this symbol.
    /// </summary>
    internal (decimal Price, DateTime ReceivedAt)? GetWsLastTradePrice(string symbol)
        => _wsLastTradePrice.TryGetValue(symbol, out var v) ? v : null;

    /// <summary>
    /// Returns the in-memory WS book bid/ask levels for <paramref name="symbol"/>,
    /// or null if no book data has been received for this symbol.
    /// </summary>
    internal (decimal Bid, decimal Ask)? GetWsBookLevels(string symbol)
        => _wsBookLevels.TryGetValue(symbol, out var v) ? v : null;

    /// <summary>
    /// Returns the multi-level WS-cached book for the symbol plus the timestamp the snapshot was received.
    /// Returns <c>null</c> when the symbol has not received a multi-level update.
    /// Bids returned in DESCENDING-price order (best bid first); asks in ASCENDING-price order (best ask first).
    /// </summary>
    public (IReadOnlyList<(decimal Price, decimal Size)> Bids, IReadOnlyList<(decimal Price, decimal Size)> Asks, DateTime ReceivedAt)?
        GetWsBookLadder(string symbol)
        => _wsBookLadder.TryGetValue(symbol, out var ladder)
            ? (ladder.Bids, ladder.Asks, ladder.ReceivedAt)
            : null;

    // ── Test seams ──

    /// <summary>
    /// Test seam: inject a fresh last-trade-price for <paramref name="symbol"/> as if it
    /// arrived via the WS market_stats feed right now.
    /// </summary>
    internal void SimulateWsLastTradePrice(string symbol, decimal price)
        => _wsLastTradePrice[symbol] = (price, DateTime.UtcNow);

    /// <summary>
    /// Test seam: inject WS order-book bid/ask levels for <paramref name="symbol"/> as if
    /// they arrived via the WS market_stats feed.
    /// </summary>
    internal void SimulateWsBookLevels(string symbol, decimal bid, decimal ask)
        => _wsBookLevels[symbol] = (bid, ask);

    /// <summary>
    /// Test seam: inject a multi-level WS book ladder for <paramref name="symbol"/> as if
    /// it arrived via the WS feed. Bids should be in descending-price order; asks ascending.
    /// </summary>
    internal void SimulateWsBookLadder(
        string symbol,
        IEnumerable<(decimal Price, decimal Size)> bids,
        IEnumerable<(decimal Price, decimal Size)> asks,
        DateTime? receivedAt = null)
        => _wsBookLadder[symbol] = (
            bids.ToList(),
            asks.ToList(),
            receivedAt ?? DateTime.UtcNow);

    public async Task StartAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        // Load market index → symbol mappings via REST
        await LoadMarketMappingsAsync(ct);

        // Connect and subscribe
        await _wsClient.ConnectAsync(ct);
        await _wsClient.SubscribeAsync("market_stats/all", ct: ct);

        _wsClient.OnMessage += HandleMessage;
        _wsClient.OnDisconnected += reason => OnDisconnected?.Invoke(ExchangeName, reason);

        _logger.LogInformation("Lighter WebSocket stream started with {Count} market mappings",
            _marketIndexToSymbol.Count);
    }

    private async Task LoadMarketMappingsAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("LighterMetadata");
        client.BaseAddress ??= new Uri("https://mainnet.zklighter.elliot.ai/api/v1/");
        client.Timeout = TimeSpan.FromSeconds(15);

        var response = await client.GetAsync("orderBookDetails?filter=perp", ct);
        response.EnsureSuccessStatusCode();

        var details = await response.Content
            .ReadFromJsonAsync<LighterOrderBookDetailsResponse>(JsonOptions, ct);

        if (details?.OrderBookDetails is null)
        {
            return;
        }

        foreach (var market in details.OrderBookDetails)
        {
            _marketIndexToSymbol[market.MarketId] = market.Symbol;
        }
    }

    private void HandleMessage(string channel, JsonElement payload)
    {
        if (!channel.StartsWith("market_stats", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            // market_stats can return a single object or the payload may contain a "data" wrapper
            if (payload.TryGetProperty("data", out var dataEl))
            {
                ParseMarketStatsElement(dataEl);
            }
            else
            {
                ParseMarketStatsElement(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Lighter market_stats message");
        }
    }

    private void ParseMarketStatsElement(JsonElement el)
    {
        // Could be a single object or an array
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                TryParseMarketStat(item);
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            TryParseMarketStat(el);
        }
    }

    internal void TryParseMarketStat(JsonElement el)
    {
        // Extract market_index to look up symbol
        if (!el.TryGetProperty("market_index", out var marketIndexProp))
        {
            return;
        }

        var marketIndex = marketIndexProp.ValueKind == JsonValueKind.Number
            ? marketIndexProp.GetInt32()
            : int.TryParse(marketIndexProp.GetString(), out var parsed) ? parsed : -1;

        if (marketIndex < 0 || !_marketIndexToSymbol.TryGetValue(marketIndex, out var symbol))
        {
            return;
        }

        // Extract funding rate (8-hour rate from API, divide by 8 for hourly)
        var fundingRate = GetDecimalProperty(el, "funding_rate_current")
                       ?? GetDecimalProperty(el, "funding_rate")
                       ?? 0m;

        // Index price used as mark price (matches LighterConnector behavior)
        var indexPrice = GetDecimalProperty(el, "index_price") ?? 0m;
        var markPrice = GetDecimalProperty(el, "mark_price") ?? indexPrice;
        var volume = GetDecimalProperty(el, "volume_24h")
                  ?? GetDecimalProperty(el, "daily_quote_token_volume")
                  ?? 0m; // Cache preserves REST-fetched volume when this is 0

        if (volume == 0m)
        {
            _logger.LogDebug("No volume in WS payload for {Symbol} — cache will preserve REST value", symbol);
        }

        var dto = new FundingRateDto
        {
            ExchangeName = ExchangeName,
            Symbol = symbol,
            RawRate = fundingRate,
            RatePerHour = fundingRate / 8m,
            MarkPrice = markPrice,
            IndexPrice = indexPrice,
            Volume24hUsd = volume,
        };

        _cache.Update(dto);
        OnRateUpdate?.Invoke(dto);

        // Multi-level book ladder: try to parse a "bids"/"asks" array if present in the WS payload.
        // Falls back to top-of-book (best_bid / best_ask) as a single-level ladder.
        var parsedBids = TryParseBookSideArray(el, "bids");
        var parsedAsks = TryParseBookSideArray(el, "asks");

        if (parsedBids is null && parsedAsks is null)
        {
            // Fall back to top-of-book as a single-entry ladder.
            var bid = GetDecimalProperty(el, "best_bid");
            var ask = GetDecimalProperty(el, "best_ask");

            if (bid is > 0m || ask is > 0m)
            {
                parsedBids = bid is > 0m ? new List<(decimal, decimal)> { (bid.Value, 0m) } : new List<(decimal, decimal)>();
                parsedAsks = ask is > 0m ? new List<(decimal, decimal)> { (ask.Value, 0m) } : new List<(decimal, decimal)>();
            }
        }

        if (parsedBids is not null && parsedAsks is not null)
        {
            // Sort: bids descending (best bid first), asks ascending (best ask first).
            parsedBids.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            parsedAsks.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            _wsBookLadder[symbol] = (parsedBids, parsedAsks, DateTime.UtcNow);
        }
    }

    private static List<(decimal Price, decimal Size)>? TryParseBookSideArray(JsonElement el, string property)
    {
        if (!el.TryGetProperty(property, out var sideEl) || sideEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<(decimal Price, decimal Size)>();
        foreach (var level in sideEl.EnumerateArray())
        {
            var price = GetDecimalProperty(level, "price") ?? GetDecimalProperty(level, "p");
            var size = GetDecimalProperty(level, "size") ?? GetDecimalProperty(level, "s") ?? 0m;
            if (price is > 0m)
            {
                result.Add((price.Value, size));
            }
        }

        return result;
    }

    private static decimal? GetDecimalProperty(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetDecimal();
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
        {
            return val;
        }

        return null;
    }

    public async Task StopAsync()
    {
        _wsClient.OnMessage -= HandleMessage;
        await _wsClient.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        GC.SuppressFinalize(this);
    }
}
