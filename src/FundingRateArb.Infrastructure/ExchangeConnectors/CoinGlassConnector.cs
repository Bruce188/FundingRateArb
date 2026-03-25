using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Read-only funding rate aggregator connector using the CoinGlass public API.
/// Provides funding rates from major exchanges (Binance, Bybit, OKX, dYdX, etc.)
/// that are not directly connected via dedicated connectors.
///
/// Trade operations (PlaceMarketOrder, ClosePosition, GetAvailableBalance) are not
/// supported — this is a data source only.
/// </summary>
public class CoinGlassConnector : IExchangeConnector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly ILogger<CoinGlassConnector> _logger;
    private readonly string? _apiKey;

    /// <summary>
    /// Exchanges that already have dedicated connectors. Rates from these
    /// exchanges are skipped by the aggregator to avoid duplicates.
    /// </summary>
    private static readonly HashSet<string> DirectConnectorExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hyperliquid", "Lighter", "Aster"
    };

    /// <summary>Common symbol suffixes to strip during normalization.</summary>
    private static readonly string[] SymbolSuffixes = ["USDT", "USD", "-PERP", "_PERP", "/USD", "/USDT"];

    public CoinGlassConnector(HttpClient httpClient, IConfiguration configuration, ILogger<CoinGlassConnector> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["ExchangeConnectors:CoinGlass:ApiKey"];
    }

    public string ExchangeName => "CoinGlass";

    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/futures/funding-rates-all");
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add("CG-API-KEY", _apiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CoinGlass API request failed");
            return [];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CoinGlass API returned {StatusCode}", response.StatusCode);
                return [];
            }

            CoinGlassResponse? data;
            try
            {
                data = await response.Content.ReadFromJsonAsync<CoinGlassResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse CoinGlass response");
                return [];
            }

            if (data?.Data is null || data.Code != "0")
            {
                _logger.LogDebug("CoinGlass returned no data or error code {Code}", data?.Code);
                return [];
            }

            var rates = new List<FundingRateDto>();

            foreach (var item in data.Data)
            {
                if (string.IsNullOrEmpty(item.Symbol))
                {
                    continue;
                }

                // Normalize symbol: remove trailing "USDT", "USD", "PERP" suffixes
                var symbol = NormalizeSymbol(item.Symbol);

                if (item.FundingRateByExchange is null)
                {
                    continue;
                }

                foreach (var (exchangeName, exchangeRate) in item.FundingRateByExchange)
                {
                    // Skip exchanges with dedicated connectors
                    if (DirectConnectorExchanges.Contains(exchangeName))
                    {
                        continue;
                    }

                    if (exchangeRate?.Rate is null)
                    {
                        continue;
                    }

                    var rawRate = exchangeRate.Rate.Value;

                    // CoinGlass rates are per-interval (typically 8h). Convert to per-hour.
                    var intervalHours = exchangeRate.IntervalHours > 0 ? exchangeRate.IntervalHours : 8;
                    var ratePerHour = rawRate / intervalHours;

                    rates.Add(new FundingRateDto
                    {
                        // Design decision: all rates map to the "CoinGlass" exchange entity.
                        // This means cross-source arbitrage (e.g., Binance vs. OKX) within CoinGlass
                        // data will not be detected — only spreads between CoinGlass and directly
                        // connected exchanges (Hyperliquid, Lighter, Aster) are surfaced.
                        // To enable cross-source arbitrage, each source exchange would need its own
                        // ExchangeId, which requires additional Exchange entity seeding and
                        // deduplication logic in FundingRateFetcher.
                        ExchangeName = "CoinGlass",
                        Symbol = symbol,
                        RawRate = rawRate,
                        RatePerHour = ratePerHour,
                        MarkPrice = exchangeRate.MarkPrice ?? 0,
                        IndexPrice = exchangeRate.IndexPrice ?? 0,
                        Volume24hUsd = item.Volume24hUsd ?? 0,
                    });
                }
            }

            _logger.LogInformation("CoinGlass aggregator returned {Count} funding rates", rates.Count);

            return rates;

        } // end using response
    }

    public Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        throw new NotSupportedException("CoinGlass is a read-only aggregator. Mark prices are included in funding rate data.");
    }

    public Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        throw new NotSupportedException("CoinGlass is a read-only aggregator, not a trading venue.");
    }

    public Task<OrderResultDto> PlaceMarketOrderAsync(string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        throw new NotSupportedException("CoinGlass is a read-only aggregator, not a trading venue.");
    }

    public Task<OrderResultDto> ClosePositionAsync(string asset, Side side, CancellationToken ct = default)
    {
        throw new NotSupportedException("CoinGlass is a read-only aggregator, not a trading venue.");
    }

    public Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
    {
        return Task.FromResult<int?>(null);
    }

    public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
    {
        return Task.FromResult<DateTime?>(null);
    }

    /// <summary>
    /// Normalizes exchange-specific symbols to a common format.
    /// e.g., "BTCUSDT" -> "BTC", "ETH-USD-PERP" -> "ETH"
    /// </summary>
    private static string NormalizeSymbol(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        // Strip common suffixes
        foreach (var suffix in SymbolSuffixes)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                s = s[..^suffix.Length];
                break;
            }
        }
        // Strip trailing separator if present
        return s.TrimEnd('-', '_', '/');
    }

    // ── Response DTOs ──────────────────────────────────────────────

    private class CoinGlassResponse
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("data")]
        public List<CoinGlassItem>? Data { get; set; }
    }

    private class CoinGlassItem
    {
        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }

        [JsonPropertyName("volume24hUsd")]
        public decimal? Volume24hUsd { get; set; }

        /// <summary>
        /// Map of exchange name to funding rate data for this symbol.
        /// </summary>
        [JsonPropertyName("fundingRateByExchange")]
        public Dictionary<string, CoinGlassExchangeRate?>? FundingRateByExchange { get; set; }
    }

    private class CoinGlassExchangeRate
    {
        [JsonPropertyName("rate")]
        public decimal? Rate { get; set; }

        [JsonPropertyName("markPrice")]
        public decimal? MarkPrice { get; set; }

        [JsonPropertyName("indexPrice")]
        public decimal? IndexPrice { get; set; }

        /// <summary>Funding interval in hours (default 8 for most CEXes).</summary>
        [JsonPropertyName("intervalHours")]
        public int IntervalHours { get; set; } = 8;
    }
}
