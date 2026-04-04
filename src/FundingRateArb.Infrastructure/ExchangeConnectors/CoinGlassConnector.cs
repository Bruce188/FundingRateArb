using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
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
    private readonly ICoinGlassAnalyticsRepository _analyticsRepo;
    private readonly string? _apiKey;
    private static readonly object BackoffLock = new();
    private static int _consecutiveFailures;
    private static DateTime _backoffUntil = DateTime.MinValue;
    private static int _lastLoggedBackoffLevel;

    /// <summary>Resets static backoff state. For unit testing only.</summary>
    internal static void ResetBackoffState()
    {
        lock (BackoffLock)
        {
            _consecutiveFailures = 0;
            _backoffUntil = DateTime.MinValue;
            _lastLoggedBackoffLevel = 0;
        }
    }

    /// <summary>
    /// Exchanges that already have dedicated connectors. Rates from these
    /// exchanges are skipped by the aggregator to avoid duplicates.
    /// </summary>
    internal static readonly HashSet<string> DirectConnectorExchanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hyperliquid", "Lighter", "Aster", "Binance"
    };

    /// <summary>Common symbol suffixes to strip during normalization.
    /// Ordered longest-first so compound suffixes (e.g., "USD_PERP") are matched before simple ones.</summary>
    private static readonly string[] SymbolSuffixes = ["/USDT", "/USD", "USDT_PERP", "USD_PERP", "USDT-PERP", "USD-PERP", "-PERP", "_PERP", "USDT", "USD"];

    public CoinGlassConnector(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CoinGlassConnector> logger,
        ICoinGlassAnalyticsRepository analyticsRepo)
    {
        _httpClient = httpClient;
        _logger = logger;
        _analyticsRepo = analyticsRepo;
        _apiKey = configuration["ExchangeConnectors:CoinGlass:ApiKey"];
    }

    public string ExchangeName => "CoinGlass";

    public bool IsEstimatedFillExchange => false;

    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        lock (BackoffLock)
        {
            if (DateTime.UtcNow < _backoffUntil)
            {
                _logger.LogDebug("CoinGlass API in backoff until {BackoffUntil} ({Failures} consecutive failures)",
                    _backoffUntil, _consecutiveFailures);
                return [];
            }
        }

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
            RecordFailure();
            return [];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CoinGlass API returned {StatusCode}", response.StatusCode);
                RecordFailure();
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
                RecordFailure();
                return [];
            }

            if (data?.Data is null || data.Code != "0")
            {
                _logger.LogDebug("CoinGlass returned no data or error code {Code}", data?.Code);
                RecordFailure();
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

            // Reset backoff on success
            lock (BackoffLock)
            {
                if (_consecutiveFailures > 0)
                {
                    _logger.LogInformation("CoinGlass API recovered after {Failures} consecutive failures",
                        _consecutiveFailures);
                }
                _consecutiveFailures = 0;
                _backoffUntil = DateTime.MinValue;
                _lastLoggedBackoffLevel = 0;
            }

            // Store unfiltered per-exchange rates for analytics (side-effect, non-blocking)
            try
            {
                var analyticsRates = new List<Domain.Entities.CoinGlassExchangeRate>();
                foreach (var item in data.Data)
                {
                    if (string.IsNullOrEmpty(item.Symbol) || item.FundingRateByExchange is null)
                        continue;

                    var symbol = NormalizeSymbol(item.Symbol);
                    foreach (var (exchangeName, exchRate) in item.FundingRateByExchange)
                    {
                        if (exchRate?.Rate is null)
                            continue;

                        var intervalHours = exchRate.IntervalHours > 0 ? exchRate.IntervalHours : 8;
                        analyticsRates.Add(new Domain.Entities.CoinGlassExchangeRate
                        {
                            SnapshotTime = DateTime.UtcNow,
                            SourceExchange = exchangeName,
                            Symbol = symbol,
                            RawRate = exchRate.Rate.Value,
                            RatePerHour = exchRate.Rate.Value / intervalHours,
                            IntervalHours = intervalHours,
                            MarkPrice = exchRate.MarkPrice ?? 0,
                            IndexPrice = exchRate.IndexPrice ?? 0,
                            Volume24hUsd = item.Volume24hUsd ?? 0
                        });
                    }
                }

                if (analyticsRates.Count > 0)
                {
                    // Discovery detection
                    var knownPairs = await _analyticsRepo.GetKnownPairsAsync(ct);
                    var discoveryEvents = new List<CoinGlassDiscoveryEvent>();

                    if (knownPairs.Count == 0)
                    {
                        _logger.LogInformation("CoinGlass analytics: first run, skipping discovery detection (no baseline)");
                    }
                    else
                    {
                        var currentExchanges = analyticsRates.Select(r => r.SourceExchange).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var knownExchanges = knownPairs.Select(p => p.Exchange).ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var exchange in currentExchanges.Except(knownExchanges, StringComparer.OrdinalIgnoreCase))
                        {
                            var coinCount = analyticsRates.Count(r => r.SourceExchange.Equals(exchange, StringComparison.OrdinalIgnoreCase));
                            _logger.LogWarning("CoinGlass: new exchange discovered: {ExchangeName} with {CoinCount} coins", exchange, coinCount);
                            discoveryEvents.Add(new CoinGlassDiscoveryEvent
                            {
                                EventType = DiscoveryEventType.NewExchange,
                                ExchangeName = exchange,
                                DiscoveredAt = DateTime.UtcNow
                            });
                        }

                        // New coins on known exchanges
                        foreach (var rate in analyticsRates)
                        {
                            if (!knownPairs.Contains((rate.SourceExchange, rate.Symbol)))
                            {
                                if (DirectConnectorExchanges.Contains(rate.SourceExchange))
                                {
                                    _logger.LogInformation("CoinGlass: new coin {Symbol} available on {ExchangeName} (has connector)", rate.Symbol, rate.SourceExchange);
                                }
                                discoveryEvents.Add(new CoinGlassDiscoveryEvent
                                {
                                    EventType = DiscoveryEventType.NewCoin,
                                    ExchangeName = rate.SourceExchange,
                                    Symbol = rate.Symbol,
                                    DiscoveredAt = DateTime.UtcNow
                                });
                            }
                        }
                    }

                    await _analyticsRepo.SaveSnapshotAsync(analyticsRates, ct);
                    if (discoveryEvents.Count > 0)
                        await _analyticsRepo.SaveDiscoveryEventsAsync(discoveryEvents, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CoinGlass analytics storage failed — continuing with rate data");
            }

            return rates;

        } // end using response
    }

    private void RecordFailure()
    {
        lock (BackoffLock)
        {
            _consecutiveFailures++;
            var cappedFailures = Math.Min(_consecutiveFailures, 14); // cap to prevent 2^n overflow
            var backoffSeconds = Math.Min(60 * (1 << (cappedFailures - 1)), 900);
            _backoffUntil = DateTime.UtcNow.AddSeconds(backoffSeconds);

            // Only log on first failure or when backoff level changes
            if (_consecutiveFailures == 1 || cappedFailures != _lastLoggedBackoffLevel)
            {
                _lastLoggedBackoffLevel = cappedFailures;
                _logger.LogWarning("CoinGlass API failure #{Count} — backing off for {Seconds}s",
                    _consecutiveFailures, backoffSeconds);
            }
        }
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

    // Read-only aggregator — cannot verify position state; return null (unknown) so reconciliation skips
    public Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
        => Task.FromResult<bool?>(null);

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
        public Dictionary<string, CoinGlassExchangeRateDto?>? FundingRateByExchange { get; set; }
    }

    private class CoinGlassExchangeRateDto
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
