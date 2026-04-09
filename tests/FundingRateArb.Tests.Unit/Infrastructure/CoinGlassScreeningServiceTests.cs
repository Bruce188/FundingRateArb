using System.Net;
using System.Text;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class CoinGlassScreeningServiceTests : IDisposable
{
    private readonly List<HttpClient> _clientsToDispose = new();

    public void Dispose()
    {
        foreach (var client in _clientsToDispose)
        {
            client.Dispose();
        }
        _clientsToDispose.Clear();
    }

    /// <summary>
    /// Records every request the service makes and returns a queued response, so tests can
    /// verify the request URL/headers and assert how the service handles each response shape.
    /// Disposes any remaining queued responses when itself disposed (via the owning HttpClient).
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(Responses.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Any responses that were queued but never consumed — dispose to avoid leaks.
                while (Responses.Count > 0)
                {
                    Responses.Dequeue().Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    private (CoinGlassScreeningService Service, StubHandler Handler) MakeService(
        string? apiKey = "test-key",
        int investmentUsd = 10000,
        double minAprPct = 10d)
    {
        var handler = new StubHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var configValues = new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = apiKey,
            ["ExchangeConnectors:CoinGlass:ScreeningInvestmentUsd"] = investmentUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ExchangeConnectors:CoinGlass:ScreeningMinAprPct"] = minAprPct.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues!).Build();
        var service = new CoinGlassScreeningService(client, config, NullLogger<CoinGlassScreeningService>.Instance);
        return (service, handler);
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task GetHotSymbolsAsync_NoApiKey_ReturnsEmptySetWithoutCallingApi()
    {
        var (service, handler) = MakeService(apiKey: null);

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        handler.Requests.Should().BeEmpty("screening is opt-in — no key means no API call");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_EmptyApiKey_ReturnsEmptySet()
    {
        var (service, handler) = MakeService(apiKey: "");

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NonSuccessStatus_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"0\"}", HttpStatusCode.TooManyRequests));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_MalformedJson_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{not valid json"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NonZeroErrorCode_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"40001\",\"msg\":\"rate limited\",\"data\":[]}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_ItemsAboveAprThreshold_ReturnedAsHotSymbols()
    {
        var (service, handler) = MakeService(minAprPct: 50d);
        var json = """
        {
          "code": "0",
          "msg": "success",
          "data": [
            { "symbol": "BTCUSDT", "apr": 100.5, "funding": 0.5, "fee": 0.03, "spread": 0.01 },
            { "symbol": "ETHUSDT", "apr": 80.0, "funding": 0.4, "fee": 0.03, "spread": 0.01 },
            { "symbol": "DOGEUSDT", "apr": 25.0, "funding": 0.1, "fee": 0.03, "spread": 0.01 }
          ]
        }
        """;
        handler.Responses.Enqueue(JsonResponse(json));

        var result = await service.GetHotSymbolsAsync();

        result.Should().Contain("BTC", "100.5 APR > 50 threshold");
        result.Should().Contain("ETH", "80.0 APR > 50 threshold");
        result.Should().NotContain("DOGE", "25.0 APR < 50 threshold");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NormalizesUsdtUsdAndPerpSuffixes()
    {
        var (service, handler) = MakeService(minAprPct: 1d);
        var json = """
        {
          "code": "0",
          "msg": "success",
          "data": [
            { "symbol": "BTCUSDT", "apr": 50, "funding": 0, "fee": 0, "spread": 0 },
            { "symbol": "ETHUSD", "apr": 50, "funding": 0, "fee": 0, "spread": 0 },
            { "symbol": "SOL-PERP", "apr": 50, "funding": 0, "fee": 0, "spread": 0 },
            { "symbol": "AVAX_PERP", "apr": 50, "funding": 0, "fee": 0, "spread": 0 }
          ]
        }
        """;
        handler.Responses.Enqueue(JsonResponse(json));

        var result = await service.GetHotSymbolsAsync();

        result.Should().Contain("BTC");
        result.Should().Contain("ETH");
        result.Should().Contain("SOL");
        result.Should().Contain("AVAX");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_SetIsCaseInsensitive()
    {
        var (service, handler) = MakeService(minAprPct: 1d);
        handler.Responses.Enqueue(JsonResponse(
            "{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"symbol\":\"BTC\",\"apr\":100,\"funding\":0,\"fee\":0,\"spread\":0}]}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().Contain("BTC");
        result.Should().Contain("btc", "the returned set uses StringComparer.OrdinalIgnoreCase");
        result.Should().Contain("Btc");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_SendsApiKeyHeaderAndUsdQueryParameter()
    {
        var (service, handler) = MakeService(apiKey: "secret-key", investmentUsd: 25000);
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"0\",\"data\":[]}"));

        await service.GetHotSymbolsAsync();

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.RequestUri!.ToString().Should().Contain("api/futures/funding-rate/arbitrage");
        request.RequestUri.Query.Should().Contain("usd=25000");
        request.Headers.GetValues("CG-API-KEY").Should().Equal("secret-key");
    }

    [Fact]
    public async Task GetHotSymbolsAsync_EmptyDataArray_ReturnsEmptySet()
    {
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(JsonResponse("{\"code\":\"0\",\"msg\":\"\",\"data\":[]}"));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHotSymbolsAsync_NonSuccessStatusFromServer_ReturnsEmptySet()
    {
        // 503 path — goes through the !response.IsSuccessStatusCode early-return.
        var (service, handler) = MakeService();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Actually throws from SendAsync so the outer catch block runs.
    /// The previous name "NetworkException" was misleading — a 503 response is
    /// not a network exception.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused");
        }
    }

    [Fact]
    public async Task GetHotSymbolsAsync_TransportException_ReturnsEmptySetAndDoesNotThrow()
    {
        var handler = new ThrowingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://open-api-v4.coinglass.com/") };
        _clientsToDispose.Add(client);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
        }!).Build();
        var service = new CoinGlassScreeningService(client, config, NullLogger<CoinGlassScreeningService>.Instance);

        // Must not throw — the outer catch swallows HttpRequestException.
        var result = await service.GetHotSymbolsAsync();

        result.Should().BeEmpty(
            "transport exceptions must be caught and return an empty set");
    }
}
