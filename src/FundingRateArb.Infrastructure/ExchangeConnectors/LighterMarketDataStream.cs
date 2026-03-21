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

        if (details?.OrderBookDetails is null) return;

        foreach (var market in details.OrderBookDetails)
        {
            _marketIndexToSymbol[market.MarketId] = market.Symbol;
        }
    }

    private void HandleMessage(string channel, JsonElement payload)
    {
        if (!channel.StartsWith("market_stats", StringComparison.OrdinalIgnoreCase))
            return;

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
                TryParseMarketStat(item);
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            TryParseMarketStat(el);
        }
    }

    private void TryParseMarketStat(JsonElement el)
    {
        // Extract market_index to look up symbol
        if (!el.TryGetProperty("market_index", out var marketIndexProp))
            return;

        var marketIndex = marketIndexProp.ValueKind == JsonValueKind.Number
            ? marketIndexProp.GetInt32()
            : int.TryParse(marketIndexProp.GetString(), out var parsed) ? parsed : -1;

        if (marketIndex < 0 || !_marketIndexToSymbol.TryGetValue(marketIndex, out var symbol))
            return;

        // Extract funding rate (already per-hour, no conversion)
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
            _logger.LogDebug("No volume in WS payload for {Symbol} — cache will preserve REST value", symbol);

        var dto = new FundingRateDto
        {
            ExchangeName = ExchangeName,
            Symbol       = symbol,
            RawRate      = fundingRate,
            RatePerHour  = fundingRate,
            MarkPrice    = markPrice,
            IndexPrice   = indexPrice,
            Volume24hUsd = volume,
        };

        _cache.Update(dto);
        OnRateUpdate?.Invoke(dto);
    }

    private static decimal? GetDecimalProperty(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.GetDecimal();

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
            return val;

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
    }
}
