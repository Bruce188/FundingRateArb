using System.Net;
using Aster.Net.Interfaces.Clients;
using Aster.Net.Interfaces.Clients.FuturesApi;
using Aster.Net.Objects;
using Aster.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using FluentAssertions;
using FundingRateArb.Infrastructure.Services;
using HyperLiquid.Net.Interfaces.Clients;
using HyperLiquid.Net.Interfaces.Clients.FuturesApi;
using HyperLiquid.Net.Objects.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Simple HttpMessageHandler that always returns the same JSON response.
/// Shared by ExchangeSupportedSymbolsCacheTests for Lighter endpoint mocking.
/// </summary>
file sealed class StaticHttpMessageHandler : HttpMessageHandler
{
    private readonly string _json;
    private readonly HttpStatusCode _status;
    public int CallCount { get; private set; }

    public StaticHttpMessageHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _json = json;
        _status = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(new HttpResponseMessage(_status)
        {
            Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

public class ExchangeSupportedSymbolsCacheTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (Mock<IHyperLiquidRestClient> client, Mock<IHyperLiquidRestClientFuturesApiExchangeData> exchangeData)
        BuildHyperliquidMock(WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers> result)
    {
        var exchangeDataMock = new Mock<IHyperLiquidRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var futuresApiMock = new Mock<IHyperLiquidRestClientFuturesApi>();
        futuresApiMock.Setup(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IHyperLiquidRestClient>();
        clientMock.Setup(c => c.FuturesApi).Returns(futuresApiMock.Object);

        return (clientMock, exchangeDataMock);
    }

    private static (Mock<IAsterRestClient> client, Mock<IAsterRestClientFuturesApiExchangeData> exchangeData)
        BuildAsterMock(WebCallResult<AsterExchangeInfo> result)
    {
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.Setup(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.Setup(c => c.FuturesApi).Returns(futuresApiMock.Object);

        return (clientMock, exchangeDataMock);
    }

    private static WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers> HlSuccess(params string[] symbols)
    {
        var tickers = symbols.Select(s => new HyperLiquidFuturesTicker { Symbol = s }).ToArray();
        var data = new HyperLiquidFuturesExchangeInfoAndTickers
        {
            Tickers = tickers,
            ExchangeInfo = new HyperLiquidFuturesExchangeInfo
            {
                Symbols = Array.Empty<HyperLiquidFuturesSymbol>(),
            },
        };
        return new WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null, null, null,
            CryptoExchange.Net.Objects.ResultDataSource.Server, data, null);
    }

    private static WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers> HlFailure()
        => new(new ServerError("error", new ErrorInfo(ErrorType.SystemError, "network error"), null!));

    private static WebCallResult<AsterExchangeInfo> AsterSuccess(params string[] symbolNames)
    {
        var data = new AsterExchangeInfo
        {
            Symbols = symbolNames.Select(n => new AsterSymbol { Name = n, Filters = [] }).ToArray(),
        };
        return new WebCallResult<AsterExchangeInfo>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null, null, null,
            CryptoExchange.Net.Objects.ResultDataSource.Server, data, null);
    }

    private static WebCallResult<AsterExchangeInfo> AsterFailure()
        => new(new ServerError("error", new ErrorInfo(ErrorType.SystemError, "network error"), null!));

    private static string LighterJson(params string[] symbols)
    {
        var items = string.Join(",", symbols.Select((s, i) =>
            $"{{\"market_id\":{i + 1},\"symbol\":\"{s}\"}}"));
        return $"{{\"order_book_details\":[{items}]}}";
    }

    private static IHttpClientFactory BuildHttpFactory(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StaticHttpMessageHandler(json, status);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/") };
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        return mockFactory.Object;
    }

    private static ExchangeSupportedSymbolsCache BuildSut(
        Mock<IHyperLiquidRestClient>? hlClient = null,
        Mock<IAsterRestClient>? asterClient = null,
        IHttpClientFactory? httpFactory = null,
        DateTime? fixedTime = null)
    {
        // Default: Hyperliquid returns no symbols, Aster returns no symbols, Lighter returns no symbols
        var hl = hlClient ?? new Mock<IHyperLiquidRestClient>();
        if (hlClient is null)
        {
            var exchangeData = new Mock<IHyperLiquidRestClientFuturesApiExchangeData>();
            exchangeData.Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(HlSuccess());
            var futuresApi = new Mock<IHyperLiquidRestClientFuturesApi>();
            futuresApi.Setup(f => f.ExchangeData).Returns(exchangeData.Object);
            hl.Setup(c => c.FuturesApi).Returns(futuresApi.Object);
        }

        var aster = asterClient ?? new Mock<IAsterRestClient>();
        if (asterClient is null)
        {
            var exchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
            exchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(AsterSuccess());
            var futuresApi = new Mock<IAsterRestClientFuturesApi>();
            futuresApi.Setup(f => f.ExchangeData).Returns(exchangeData.Object);
            aster.Setup(c => c.FuturesApi).Returns(futuresApi.Object);
        }

        var factory = httpFactory ?? BuildHttpFactory(LighterJson());

        var sut = new ExchangeSupportedSymbolsCache(
            hl.Object,
            aster.Object,
            factory,
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);

        if (fixedTime.HasValue)
        {
            sut.TimeProvider = () => fixedTime.Value;
        }

        return sut;
    }

    // ── Test 1: Cache miss triggers metadata fetch and returns correct symbol set ──

    [Fact]
    public async Task GetSupportedSymbolsAsync_CacheMiss_TriggersMetadataFetch_ReturnsSymbols()
    {
        var (hlMock, hlExchangeData) = BuildHyperliquidMock(HlSuccess("BTC", "ETH", "SOL"));
        var asterMock = new Mock<IAsterRestClient>();
        // Aster setup just so it doesn't explode if called (it shouldn't be for Hyperliquid)
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        asterExchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsterSuccess());
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            BuildHttpFactory(LighterJson()),
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);

        var result = await sut.GetSupportedSymbolsAsync("Hyperliquid");

        result.Should().BeEquivalentTo(new[] { "BTC", "ETH", "SOL" });
        hlExchangeData.Verify(
            e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 2: Cache hit within TTL does not re-fetch ──────────────────────────

    [Fact]
    public async Task GetSupportedSymbolsAsync_CacheHit_WithinTTL_DoesNotRefetch()
    {
        var fixedNow = DateTime.UtcNow;
        var (hlMock, hlExchangeData) = BuildHyperliquidMock(HlSuccess("BTC", "ETH"));

        var asterMock = new Mock<IAsterRestClient>();
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        asterExchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsterSuccess());
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            BuildHttpFactory(LighterJson()),
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);
        sut.TimeProvider = () => fixedNow;

        // First call — populates cache
        await sut.GetSupportedSymbolsAsync("Hyperliquid");

        // Advance by 5 minutes (well within 30-min TTL)
        sut.TimeProvider = () => fixedNow.AddMinutes(5);

        // Second call — should be a cache hit
        var result = await sut.GetSupportedSymbolsAsync("Hyperliquid");

        result.Should().BeEquivalentTo(new[] { "BTC", "ETH" });
        hlExchangeData.Verify(
            e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "second call within TTL must not trigger a new fetch");
    }

    // ── Test 3: TTL expiry triggers re-fetch ────────────────────────────────────

    [Fact]
    public async Task GetSupportedSymbolsAsync_AfterTTLExpiry_TriggersRefresh()
    {
        var fixedNow = DateTime.UtcNow;
        var (hlMock, hlExchangeData) = BuildHyperliquidMock(HlSuccess("BTC", "ETH"));

        var asterMock = new Mock<IAsterRestClient>();
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        asterExchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsterSuccess());
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            BuildHttpFactory(LighterJson()),
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);
        sut.TimeProvider = () => fixedNow;

        await sut.GetSupportedSymbolsAsync("Hyperliquid");

        // Advance past the 30-minute TTL
        sut.TimeProvider = () => fixedNow.AddMinutes(31);

        await sut.GetSupportedSymbolsAsync("Hyperliquid");

        hlExchangeData.Verify(
            e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "after TTL expiry a second fetch must occur");
    }

    // ── Test 4: Upstream failure after successful load returns last-known-good ───

    [Fact]
    public async Task GetSupportedSymbolsAsync_UpstreamFailure_AfterSuccess_ReturnsLastKnownGood()
    {
        var fixedNow = DateTime.UtcNow;
        var callCount = 0;
        var (hlMock, hlExchangeData) = BuildHyperliquidMock(HlSuccess("BTC", "ETH")); // initial success

        // Override: first call succeeds, second call (after TTL) fails
        hlExchangeData
            .Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? HlSuccess("BTC", "ETH") : HlFailure();
            });

        var asterMock = new Mock<IAsterRestClient>();
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        asterExchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsterSuccess());
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            BuildHttpFactory(LighterJson()),
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);
        sut.TimeProvider = () => fixedNow;

        // First call — succeeds
        await sut.GetSupportedSymbolsAsync("Hyperliquid");

        // Advance past TTL so next call triggers a refresh
        sut.TimeProvider = () => fixedNow.AddMinutes(31);

        // Second call — upstream now fails, should return last-known-good
        var result = await sut.GetSupportedSymbolsAsync("Hyperliquid");

        result.Should().BeEquivalentTo(new[] { "BTC", "ETH" },
            "last-known-good should be returned when upstream fails after a prior success");
    }

    // ── Test 5: Upstream failure on first load returns empty set ────────────────

    [Fact]
    public async Task GetSupportedSymbolsAsync_UpstreamFailure_OnFirstLoad_ReturnsEmptySet()
    {
        var (hlMock, _) = BuildHyperliquidMock(HlFailure());

        var asterMock = new Mock<IAsterRestClient>();
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        asterExchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsterSuccess());
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            BuildHttpFactory(LighterJson()),
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);

        var result = await sut.GetSupportedSymbolsAsync("Hyperliquid");

        result.Should().NotBeNull("should return an empty set, not null");
        result.Should().BeEmpty("no prior data — empty set is the safe fallback");
    }

    // ── Test 6: Per-exchange isolation ──────────────────────────────────────────

    [Fact]
    public async Task GetSupportedSymbolsAsync_PerExchangeIsolation_HyperliquidMissDoesNotFetchLighter()
    {
        var hlCallCount = 0;
        var (hlMock, hlExchangeData) = BuildHyperliquidMock(HlSuccess("BTC"));
        hlExchangeData
            .Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                hlCallCount++;
                return HlSuccess("BTC");
            });

        var asterMock = new Mock<IAsterRestClient>();
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        int asterCallCount = 0;
        asterExchangeData
            .Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                asterCallCount++;
                return AsterSuccess("BTC");
            });
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var trackingHandler = new StaticHttpMessageHandler(LighterJson("SOL"));
        var lighterClient = new HttpClient(trackingHandler) { BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/") };
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(lighterClient);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            httpFactory.Object,
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);

        // Query Hyperliquid — should NOT trigger Aster or Lighter fetch
        var hlResult = await sut.GetSupportedSymbolsAsync("Hyperliquid");

        hlResult.Should().BeEquivalentTo(new[] { "BTC" });
        hlCallCount.Should().Be(1, "Hyperliquid should have been fetched once");
        asterCallCount.Should().Be(0, "Aster should NOT have been fetched when Hyperliquid is queried");
        trackingHandler.CallCount.Should().Be(0, "Lighter should NOT have been fetched when Hyperliquid is queried");

        // Now query Lighter — should trigger only Lighter fetch
        var lighterResult = await sut.GetSupportedSymbolsAsync("Lighter");

        lighterResult.Should().BeEquivalentTo(new[] { "SOL" });
        hlCallCount.Should().Be(1, "Hyperliquid should still be at 1 fetch");
        trackingHandler.CallCount.Should().Be(1, "Lighter should have been fetched once");
    }

    // ── Test 7: Case-insensitive symbol matching ────────────────────────────────

    [Fact]
    public async Task GetSupportedSymbolsAsync_ReturnsHashSetWithOrdinalIgnoreCase()
    {
        var (hlMock, _) = BuildHyperliquidMock(HlSuccess("BTC", "ETH"));

        var asterMock = new Mock<IAsterRestClient>();
        var asterExchangeData = new Mock<IAsterRestClientFuturesApiExchangeData>();
        asterExchangeData.Setup(e => e.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsterSuccess());
        var asterFuturesApi = new Mock<IAsterRestClientFuturesApi>();
        asterFuturesApi.Setup(f => f.ExchangeData).Returns(asterExchangeData.Object);
        asterMock.Setup(c => c.FuturesApi).Returns(asterFuturesApi.Object);

        var sut = new ExchangeSupportedSymbolsCache(
            hlMock.Object, asterMock.Object,
            BuildHttpFactory(LighterJson()),
            NullLogger<ExchangeSupportedSymbolsCache>.Instance);

        var result = await sut.GetSupportedSymbolsAsync("Hyperliquid");

        result.Contains("btc").Should().BeTrue("HashSet should use OrdinalIgnoreCase");
        result.Contains("BTC").Should().BeTrue("HashSet should contain the original casing");
        result.Contains("Btc").Should().BeTrue("HashSet should match any casing");
    }
}
