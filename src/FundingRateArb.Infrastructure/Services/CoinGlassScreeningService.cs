using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Calls the CoinGlass v4 /api/futures/funding-rate/arbitrage endpoint to retrieve
/// pre-calculated cross-exchange arbitrage opportunities. Returns the set of normalized
/// symbols above the configured APR threshold as a priority hint for SignalEngine.
/// Wraps the HTTP call in the shared "CoinGlass" Polly circuit-breaker pipeline and
/// exposes an <see cref="IsAvailable"/> flag so SignalEngine can skip the screening
/// step cleanly when the circuit is open.
/// </summary>
public class CoinGlassScreeningService : ICoinGlassScreeningProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] SymbolSuffixes =
        ["/USDT", "/USD", "USDT_PERP", "USD_PERP", "USDT-PERP", "USD-PERP", "-PERP", "_PERP", "USDT", "USD"];

    private readonly HttpClient _httpClient;
    private readonly ILogger<CoinGlassScreeningService> _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly string? _apiKey;
    private readonly int _investmentUsd;
    private readonly double _minAprThreshold;

    /// <inheritdoc />
    public bool IsAvailable { get; private set; } = true;

    public CoinGlassScreeningService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CoinGlassScreeningService> logger,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _httpClient = httpClient;
        _logger = logger;
        _pipeline = pipelineProvider.GetPipeline("CoinGlass");
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

        HttpResponseMessage response;
        try
        {
            response = await _pipeline.ExecuteAsync(
                async token =>
                {
                    var r = await _httpClient.SendAsync(request, token);
                    if (!r.IsSuccessStatusCode)
                    {
                        string body;
                        try
                        {
                            body = await r.Content.ReadAsStringAsync(token);
                        }
                        catch (Exception readEx) when (readEx is not OperationCanceledException)
                        {
                            body = $"<body read failed: {readEx.GetType().Name}>";
                        }

                        var statusCode = r.StatusCode;
                        r.Dispose();
                        throw new CoinGlassScreeningFailureException(
                            (int)statusCode,
                            HttpResponseBodyLogging.TruncateAndSanitize(body));
                    }
                    return r;
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — propagate the cancellation. HTTP timeouts also surface as
            // TaskCanceledException but must be treated as failures (handled by the generic
            // catch below) so the pipeline's circuit breaker can count them.
            throw;
        }
        catch (BrokenCircuitException ex)
        {
            if (IsAvailable)
            {
                _logger.LogInformation("CoinGlass screening circuit breaker OPENED — short-circuiting requests");
            }
            IsAvailable = false;
            _logger.LogWarning("CoinGlass arbitrage screening short-circuited: {Message}",
                HttpResponseBodyLogging.TruncateAndSanitize(ex.Message));
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (CoinGlassScreeningFailureException ex)
        {
            _logger.LogWarning("CoinGlass arbitrage screening returned {StatusCode}: {Body}",
                ex.StatusCode,
                ex.SanitizedBody);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CoinGlass arbitrage screening request failed: {Body}",
                HttpResponseBodyLogging.TruncateAndSanitize(ex.Message));
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        using (response)
        {
            CoinGlassArbitrageResponse? data;
            try
            {
                data = await response.Content.ReadFromJsonAsync<CoinGlassArbitrageResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                string body;
                try
                {
                    body = await response.Content.ReadAsStringAsync(ct);
                }
                catch (Exception readEx) when (readEx is not OperationCanceledException)
                {
                    body = ex.Message;
                }

                _logger.LogWarning(ex, "Failed to parse CoinGlass arbitrage response: {Body}",
                    HttpResponseBodyLogging.TruncateAndSanitize(body));
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (data?.Data is null || data.Code != "0")
            {
                _logger.LogDebug("CoinGlass arbitrage endpoint returned no data or error code {Code}", data?.Code);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Flip IsAvailable back on after a successful call — the circuit closed post
            // half-open, so downstream consumers can safely trust the fresh data.
            if (!IsAvailable)
            {
                _logger.LogInformation("CoinGlass screening circuit breaker CLOSED — requests flowing again");
                IsAvailable = true;
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

    /// <summary>
    /// Sentinel exception thrown from inside the Polly pipeline on non-2xx responses so the
    /// shared "CoinGlass" circuit breaker (which handles exceptions, not status codes) counts
    /// HTTP error responses as failures alongside network exceptions.
    /// </summary>
#pragma warning disable CA1064 // Exceptions should be public — intentionally file-scoped sentinel
#pragma warning disable CA1032 // Standard exception constructors — intentionally minimal
    private sealed class CoinGlassScreeningFailureException : Exception
#pragma warning restore CA1032
#pragma warning restore CA1064
    {
        public int StatusCode { get; }
        public string SanitizedBody { get; }

        public CoinGlassScreeningFailureException(int statusCode, string sanitizedBody)
            : base($"CoinGlass screening returned {statusCode}")
        {
            StatusCode = statusCode;
            SanitizedBody = sanitizedBody;
        }
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
