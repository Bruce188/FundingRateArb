using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FundingRateArb.Application.Common.Exchanges;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Calls the CoinGlass v4 /api/futures/funding-rate/arbitrage endpoint to retrieve
/// pre-calculated cross-exchange arbitrage opportunities. Returns the set of normalized
/// symbols above the configured APR threshold as a priority hint for SignalEngine.
/// </summary>
public class CoinGlassScreeningService : ICoinGlassScreeningProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] SymbolSuffixes =
        ["/USDT", "/USD", "USDT_PERP", "USD_PERP", "USDT-PERP", "USD-PERP", "-PERP", "_PERP", "USDT", "USD"];

    private readonly HttpClient _httpClient;
    private readonly ILogger<CoinGlassScreeningService> _logger;
    private readonly string? _apiKey;
    private readonly int _investmentUsd;
    private readonly double _minAprThreshold;

    public CoinGlassScreeningService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CoinGlassScreeningService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["ExchangeConnectors:CoinGlass:ApiKey"];
        _investmentUsd = configuration.GetValue<int?>("ExchangeConnectors:CoinGlass:ScreeningInvestmentUsd") ?? 10000;
        _minAprThreshold = configuration.GetValue<double?>("ExchangeConnectors:CoinGlass:ScreeningMinAprPct") ?? 10d;
    }

    public async Task<IReadOnlySet<string>> GetHotSymbolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            // Screening is opt-in — no key means feature disabled.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var requestUri = $"api/futures/funding-rate/arbitrage?usd={_investmentUsd}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("CG-API-KEY", _apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CoinGlass arbitrage screening returned {StatusCode}", response.StatusCode);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            CoinGlassArbitrageResponse? data;
            try
            {
                data = await response.Content.ReadFromJsonAsync<CoinGlassArbitrageResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse CoinGlass arbitrage response");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (data?.Data is null || data.Code != "0")
            {
                _logger.LogDebug("CoinGlass arbitrage endpoint returned no data or error code {Code}", data?.Code);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var hot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.Data)
            {
                if (string.IsNullOrEmpty(item.Symbol))
                {
                    continue;
                }

                if (item.Apr >= _minAprThreshold)
                {
                    hot.Add(NormalizeSymbol(item.Symbol));
                }
            }

            _logger.LogInformation(
                "CoinGlass screening returned {Count} hot symbols above {Apr}% APR (investment ${Usd})",
                hot.Count, _minAprThreshold, _investmentUsd);

            return hot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CoinGlass arbitrage screening request failed");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeSymbol(string symbol)
    {
        var normalized = symbol.Trim().ToUpperInvariant();
        foreach (var suffix in SymbolSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized;
    }

    private sealed class CoinGlassArbitrageResponse
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [JsonPropertyName("data")]
        public List<CoinGlassArbitrageItem>? Data { get; set; }
    }

    private sealed class CoinGlassArbitrageItem
    {
        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }

        [JsonPropertyName("apr")]
        public double Apr { get; set; }

        [JsonPropertyName("funding")]
        public double Funding { get; set; }

        [JsonPropertyName("fee")]
        public double Fee { get; set; }

        [JsonPropertyName("spread")]
        public double Spread { get; set; }

        [JsonPropertyName("next_funding_time")]
        public long NextFundingTime { get; set; }
    }
}
