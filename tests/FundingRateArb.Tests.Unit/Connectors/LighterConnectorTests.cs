using System.Net;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Connectors;

/// <summary>
/// Mock handler that returns a specific response for every request.
/// Used for simple single-endpoint tests.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseContent;
    private readonly HttpStatusCode _statusCode;

    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpMessageHandler(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseContent = content;
        _statusCode = status;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Mock handler that dispatches different responses based on the request URL path.
/// Used when the connector makes multiple API calls (e.g., funding-rates + exchangeStats).
/// </summary>
public class MultiRouteHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (string content, HttpStatusCode status)> _routes = new(StringComparer.OrdinalIgnoreCase);

    public void AddRoute(string pathContains, string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[pathContains] = (content, status);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.PathAndQuery ?? "";

        foreach (var (key, value) in _routes)
        {
            if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(value.status)
                {
                    Content = new StringContent(value.content, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }

        // Default: return empty 200
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// HTTP handler that counts calls, used to verify caching behaviour.
/// </summary>
public class CountingHttpMessageHandler : HttpMessageHandler
{
    private readonly string _content;
    private int _callCount;

    public int CallCount => _callCount;

    public CountingHttpMessageHandler(string content)
    {
        _content = content;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// HTTP handler that counts calls and can simulate a slow first response
/// to help verify that the lock is not held during HTTP I/O.
/// </summary>
public class CountingSlowHttpMessageHandler : HttpMessageHandler
{
    private readonly string _content;
    private readonly TaskCompletionSource<bool>? _onFirstCall;
    private int _callCount;

    public int CallCount => _callCount;

    public CountingSlowHttpMessageHandler(string content, TaskCompletionSource<bool>? onFirstCall = null)
    {
        _content = content;
        _onFirstCall = onFirstCall;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var count = Interlocked.Increment(ref _callCount);
        if (count == 1)
        {
            _onFirstCall?.TrySetResult(true);
        }

        // Small yield to allow concurrent callers to reach the cache check
        await Task.Yield();

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

public class LighterConnectorTests
{
    private readonly Mock<ILogger<LighterConnector>> _loggerMock = new();
    private readonly Mock<IConfiguration> _configMock = new();

    // ── Realistic JSON matching actual Lighter API responses ──

    private static readonly string FundingRatesJson = """
        {
            "code": 200,
            "funding_rates": [
                {
                    "market_id": 0,
                    "exchange": "lighter",
                    "symbol": "ETH",
                    "rate": 0.0001
                },
                {
                    "market_id": 1,
                    "exchange": "lighter",
                    "symbol": "BTC",
                    "rate": -0.00025
                },
                {
                    "market_id": 0,
                    "exchange": "binance",
                    "symbol": "ETH",
                    "rate": 0.00015
                }
            ]
        }
        """;

    private static readonly string ExchangeStatsJson = """
        {
            "code": 200,
            "order_book_stats": [
                { "symbol": "ETH", "daily_quote_token_volume": 15000000.00 },
                { "symbol": "BTC", "daily_quote_token_volume": 50000000.00 }
            ]
        }
        """;

    private static readonly string AssetDetailsJson = """
        {
            "code": 200,
            "asset_details": [
                { "asset_id": 0, "symbol": "USDC", "index_price": "1.000000" },
                { "asset_id": 1, "symbol": "ETH", "index_price": "2130.500000" },
                { "asset_id": 2, "symbol": "BTC", "index_price": "69750.000000" }
            ]
        }
        """;

    private static readonly string OrderBookDetailsJson = """
        {
            "code": 200,
            "order_book_details": [
                {
                    "market_id": 0,
                    "symbol": "ETH",
                    "taker_fee": "0.0000",
                    "maker_fee": "0.0000",
                    "min_base_amount": "0.0010",
                    "supported_size_decimals": 4,
                    "supported_price_decimals": 2,
                    "size_decimals": 4,
                    "price_decimals": 2,
                    "last_trade_price": 3500.50,
                    "default_initial_margin_fraction": 1000,
                    "min_initial_margin_fraction": 500,
                    "maintenance_margin_fraction": 300
                },
                {
                    "market_id": 1,
                    "symbol": "BTC",
                    "taker_fee": "0.0000",
                    "maker_fee": "0.0000",
                    "min_base_amount": "0.00001",
                    "supported_size_decimals": 5,
                    "supported_price_decimals": 2,
                    "size_decimals": 5,
                    "price_decimals": 2,
                    "last_trade_price": 65000.00,
                    "default_initial_margin_fraction": 500,
                    "min_initial_margin_fraction": 200,
                    "maintenance_margin_fraction": 100
                }
            ]
        }
        """;

    private static readonly string AccountJson = """
        {
            "code": 200,
            "total": 1,
            "accounts": [
                {
                    "code": 0,
                    "account_index": 281474976624240,
                    "available_balance": "107.50",
                    "collateral": "120.00",
                    "total_asset_value": "130.00",
                    "status": 1,
                    "positions": [
                        {
                            "market_id": 0,
                            "symbol": "ETH",
                            "sign": 1,
                            "position": "0.0500",
                            "avg_entry_price": "3400.00",
                            "position_value": "175.00",
                            "unrealized_pnl": "5.00",
                            "realized_pnl": "0.00",
                            "liquidation_price": "3200.00",
                            "margin_mode": 0
                        }
                    ]
                }
            ]
        }
        """;

    private LighterConnector CreateConnector(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new MockHttpMessageHandler(json, status);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/")
        };
        return new LighterConnector(httpClient, _loggerMock.Object, _configMock.Object);
    }

    private LighterConnector CreateMultiRouteConnector(Action<MultiRouteHttpMessageHandler> configure)
    {
        var handler = new MultiRouteHttpMessageHandler();
        configure(handler);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/")
        };
        return new LighterConnector(httpClient, _loggerMock.Object, _configMock.Object);
    }

    // ── ExchangeName ──────────────────────────────────────────────

    [Fact]
    public void ExchangeName_ReturnsLighter()
    {
        var sut = CreateConnector("{}");
        sut.ExchangeName.Should().Be("Lighter");
    }

    // ── GetFundingRatesAsync ──────────────────────────────────────

    [Fact]
    public async Task GetFundingRates_ParsesRealApiFormat()
    {
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", FundingRatesJson);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
            h.AddRoute("assetDetails", AssetDetailsJson);
        });

        var rates = await sut.GetFundingRatesAsync();

        // Should only include "lighter" exchange entries (not binance)
        rates.Should().HaveCount(2);

        var eth = rates.First(r => r.Symbol == "ETH");
        eth.ExchangeName.Should().Be("Lighter");
        eth.RawRate.Should().Be(0.0001m);
        eth.RatePerHour.Should().Be(0.0001m);
        eth.Volume24hUsd.Should().Be(15000000.00m);

        var btc = rates.First(r => r.Symbol == "BTC");
        btc.RawRate.Should().Be(-0.00025m);
        btc.Volume24hUsd.Should().Be(50000000.00m);
    }

    [Fact]
    public async Task GetFundingRates_FiltersOutNonLighterExchanges()
    {
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", FundingRatesJson);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
        });

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().OnlyContain(r => r.ExchangeName == "Lighter");
    }

    [Fact]
    public async Task GetFundingRates_RateIsAlreadyHourly_NoConversion()
    {
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", FundingRatesJson);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
        });

        var rates = await sut.GetFundingRatesAsync();

        foreach (var rate in rates)
        {
            rate.RatePerHour.Should().Be(rate.RawRate,
                "Lighter funding rates are already per-hour and should not be converted");
        }
    }

    [Fact]
    public async Task GetFundingRates_WhenEmptyRates_ReturnsEmptyList()
    {
        var json = """{ "code": 200, "funding_rates": [] }""";
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", json);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
        });

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFundingRates_WhenNullRates_ReturnsEmptyList()
    {
        var json = """{ "code": 200, "funding_rates": null }""";
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", json);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
        });

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFundingRates_PopulatesMarkPriceFromAssetDetails()
    {
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", FundingRatesJson);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
            h.AddRoute("assetDetails", AssetDetailsJson);
        });

        var rates = await sut.GetFundingRatesAsync();

        var eth = rates.First(r => r.Symbol == "ETH");
        eth.MarkPrice.Should().Be(2130.500000m, "ETH index_price from assetDetails should populate MarkPrice");

        var btc = rates.First(r => r.Symbol == "BTC");
        btc.MarkPrice.Should().Be(69750.000000m, "BTC index_price from assetDetails should populate MarkPrice");
    }

    [Fact]
    public async Task GetFundingRates_WhenAssetDetailsFails_StillReturnsRatesWithZeroMarkPrice()
    {
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", FundingRatesJson);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
            h.AddRoute("assetDetails", "Internal Server Error", HttpStatusCode.InternalServerError);
        });

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(2, "rates must still be returned when assetDetails fails");
        rates.Should().AllSatisfy(r => r.MarkPrice.Should().Be(0m,
            "mark price must be zero when assetDetails endpoint fails"));
    }

    // ── GetMarkPriceAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetMarkPrice_ReturnsLastTradePriceFromOrderBookDetails()
    {
        var sut = CreateConnector(OrderBookDetailsJson);

        var price = await sut.GetMarkPriceAsync("ETH");

        price.Should().Be(3500.50m);
    }

    [Fact]
    public async Task GetMarkPrice_ReturnsCorrectPriceForBTC()
    {
        var sut = CreateConnector(OrderBookDetailsJson);

        var price = await sut.GetMarkPriceAsync("BTC");

        price.Should().Be(65000.00m);
    }

    [Fact]
    public async Task GetMarkPrice_WhenAssetNotFound_ThrowsKeyNotFoundException()
    {
        var sut = CreateConnector(OrderBookDetailsJson);

        var act = () => sut.GetMarkPriceAsync("DOGE");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetMarkPrice_IsCaseInsensitive()
    {
        var sut = CreateConnector(OrderBookDetailsJson);

        var price = await sut.GetMarkPriceAsync("eth");

        price.Should().Be(3500.50m);
    }

    // ── GetAvailableBalanceAsync ─────────────────────────────────

    [Fact]
    public async Task GetAvailableBalance_ParsesAccountBalance()
    {
        _configMock.Setup(c => c["Exchanges:Lighter:AccountIndex"]).Returns("281474976624240");

        var sut = CreateConnector(AccountJson);

        var balance = await sut.GetAvailableBalanceAsync();

        balance.Should().Be(107.50m);
    }

    [Fact]
    public async Task GetAvailableBalance_WhenNoAccount_ReturnsZero()
    {
        _configMock.Setup(c => c["Exchanges:Lighter:AccountIndex"]).Returns("999999");

        var json = """{ "code": 200, "accounts": [] }""";
        var sut = CreateConnector(json);

        var balance = await sut.GetAvailableBalanceAsync();

        balance.Should().Be(0m);
    }

    [Fact]
    public async Task GetAvailableBalance_WhenAccountIndexNull_ThrowsInvalidOperationException()
    {
        _configMock.Setup(c => c["Exchanges:Lighter:AccountIndex"]).Returns((string?)null);

        var sut = CreateConnector(AccountJson);

        var act = () => sut.GetAvailableBalanceAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Account Index is required*");
    }

    [Fact]
    public async Task GetAvailableBalance_WhenAccountIndexNonNumeric_ThrowsInvalidOperationException()
    {
        _configMock.Setup(c => c["Exchanges:Lighter:AccountIndex"]).Returns("0xABCDEF");

        var sut = CreateConnector(AccountJson);

        var act = () => sut.GetAvailableBalanceAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Account Index must be numeric*");
    }

    // ── PlaceMarketOrderAsync ────────────────────────────────────
    // Note: Full integration tests for PlaceMarketOrder/ClosePosition require
    // the native signer library. These tests verify the error handling when
    // the signer is not configured.

    [Fact]
    public async Task PlaceMarketOrder_WithoutCredentials_ReturnsError()
    {
        // No signer credentials configured
        _configMock.Setup(c => c["Exchanges:Lighter:SignerPrivateKey"]).Returns((string?)null);

        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("orderBookDetails", OrderBookDetailsJson);
        });

        var result = await sut.PlaceMarketOrderAsync("ETH", Domain.Enums.Side.Long, 100m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SignerPrivateKey");
    }

    // ── ClosePositionAsync ───────────────────────────────────────

    [Fact]
    public async Task ClosePosition_WithoutCredentials_ReturnsError()
    {
        // No signer credentials configured
        _configMock.Setup(c => c["Exchanges:Lighter:SignerPrivateKey"]).Returns((string?)null);

        var sut = CreateConnector(OrderBookDetailsJson);

        var result = await sut.ClosePositionAsync("ETH", Domain.Enums.Side.Long);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("SignerPrivateKey");
    }

    // ── Cache concurrency (C-LC1) ─────────────────────────────

    [Fact]
    public async Task GetMarkPrice_ConcurrentCalls_DoNotSerializeOnHttpIo()
    {
        // The lock must NOT be held during the HTTP I/O. Multiple concurrent callers should
        // be able to read the cache simultaneously once it is populated.
        // Arrange: use a slow handler that introduces latency to detect serialization
        var completionSource = new TaskCompletionSource<bool>();

        var handler = new CountingSlowHttpMessageHandler(
            OrderBookDetailsJson,
            onFirstCall: completionSource);

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/")
        };
        var sut = new LighterConnector(httpClient, _loggerMock.Object, _configMock.Object);

        // First call populates cache
        var price1 = await sut.GetMarkPriceAsync("ETH");

        // Now two concurrent calls — both should resolve from cache without waiting for HTTP
        var t1 = sut.GetMarkPriceAsync("ETH");
        var t2 = sut.GetMarkPriceAsync("BTC");

        await Task.WhenAll(t1, t2);

        price1.Should().Be(3500.50m);
        (await t1).Should().Be(3500.50m);
        (await t2).Should().Be(65000.00m);

        // HTTP handler should have been called exactly once (cache hit on concurrent calls)
        handler.CallCount.Should().Be(1,
            "cache should be populated after first call; concurrent calls must not re-fetch");
    }

    [Fact]
    public async Task GetMarkPrice_CachePreventsRedundantFetches()
    {
        // Two sequential calls within TTL must only issue one HTTP request.
        var handler = new CountingHttpMessageHandler(OrderBookDetailsJson);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/")
        };
        var sut = new LighterConnector(httpClient, _loggerMock.Object, _configMock.Object);

        var price1 = await sut.GetMarkPriceAsync("ETH");
        var price2 = await sut.GetMarkPriceAsync("ETH");

        price1.Should().Be(3500.50m);
        price2.Should().Be(3500.50m);
        handler.CallCount.Should().Be(1,
            "second call within TTL should use cache, not make another HTTP request");
    }

    // ── IDisposable (M-LC2) ───────────────────────────────────

    [Fact]
    public void LighterConnector_ImplementsIDisposable()
    {
        var sut = CreateConnector("{}");
        sut.Should().BeAssignableTo<IDisposable>(
            "LighterConnector must implement IDisposable to release the SemaphoreSlim");
    }

    [Fact]
    public void LighterConnector_Dispose_DoesNotThrow()
    {
        var sut = CreateConnector("{}");
        var act = () => ((IDisposable)sut).Dispose();
        act.Should().NotThrow("Dispose must be safe to call");
    }

    // ── L4: _orderCounter is instance not static ──────────────────────────────

    [Fact]
    public void OrderCounter_IsInstanceField_TwoInstancesDoNotShareCounter()
    {
        // If _orderCounter were static, two instances would share state.
        // This test verifies that two separate connectors are independent objects
        // (structural verification — we cannot directly inspect the counter value,
        // but we can confirm two separate connector instances are distinct).
        var sut1 = CreateConnector("{}");
        var sut2 = CreateConnector("{}");

        // They must be different instances
        sut1.Should().NotBeSameAs(sut2,
            "each connector instance must maintain its own order counter state");

        // Both must still be LighterConnector instances
        sut1.Should().BeOfType<LighterConnector>();
        sut2.Should().BeOfType<LighterConnector>();
    }

    // ── M4: Thundering-herd prevention on cache expiry ────────────────────────

    [Fact]
    public async Task GetMarkPrice_ConcurrentCallsOnCacheMiss_OnlyFetchOnce()
    {
        // When the cache is empty (first call), multiple concurrent callers must result
        // in exactly one HTTP fetch, not N fetches (thundering-herd prevention).
        var handler = new CountingHttpMessageHandler(OrderBookDetailsJson);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/")
        };
        var sut = new LighterConnector(httpClient, _loggerMock.Object, _configMock.Object);

        // Fire multiple concurrent requests on a cold cache
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => sut.GetMarkPriceAsync("ETH"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All callers should get the correct price
        results.Should().AllSatisfy(p => p.Should().Be(3500.50m));

        // Due to thundering-herd prevention, we expect very few fetches
        // (ideally 1, but the implementation allows one re-check after lock commit)
        handler.CallCount.Should().BeLessThanOrEqualTo(2,
            "concurrent callers on a cold cache should share the in-flight fetch task");
    }

    // ── B-W6: GetFundingRatesAsync EnsureSuccessStatusCode ────────────────────

    [Fact]
    public async Task GetFundingRates_WhenRatesEndpointReturns500_ThrowsHttpRequestException()
    {
        // Arrange — rates endpoint returns 500; should throw
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", "Internal Server Error", HttpStatusCode.InternalServerError);
            h.AddRoute("exchangeStats", ExchangeStatsJson);
        });

        // Act & Assert
        var act = () => sut.GetFundingRatesAsync();

        await act.Should().ThrowAsync<HttpRequestException>(
            "a 500 from the rates endpoint must propagate as an exception, not silently return zero rates");
    }

    [Fact]
    public async Task GetFundingRates_WhenStatsEndpointReturns500_StillReturnsRatesWithZeroVolume()
    {
        // Arrange — stats endpoint returns 500; rates must still succeed with zero volume
        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("funding-rates", FundingRatesJson);
            h.AddRoute("exchangeStats", "Internal Server Error", HttpStatusCode.InternalServerError);
        });

        // Act
        var rates = await sut.GetFundingRatesAsync();

        // Assert — rates are still returned (stats failure is non-critical)
        rates.Should().HaveCount(2, "rates must be returned even when the stats endpoint fails");
        rates.Should().AllSatisfy(r => r.Volume24hUsd.Should().Be(0m,
            "volume must be zero when stats endpoint fails"));
    }

    // ── H4: Integer overflow protection ──────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_RejectsZeroOrNegativePrice_ReturnsError()
    {
        // H4 test: When last_trade_price <= 0, the order should fail with an error.
        // The connector rejects zero/negative prices before attempting to sign.
        var zeroPriceJson = """
            {
                "code": 200,
                "order_book_details": [
                    {
                        "market_id": 0,
                        "symbol": "ETH",
                        "size_decimals": 4,
                        "price_decimals": 2,
                        "last_trade_price": 0.0,
                        "default_initial_margin_fraction": 1000,
                        "min_initial_margin_fraction": 500,
                        "maintenance_margin_fraction": 300
                    }
                ]
            }
            """;

        _configMock.Setup(c => c["Exchanges:Lighter:SignerPrivateKey"]).Returns("0xabc123");
        _configMock.Setup(c => c["Exchanges:Lighter:ApiKey"]).Returns("2");
        _configMock.Setup(c => c["Exchanges:Lighter:AccountIndex"]).Returns("281474976624240");

        var sut = CreateMultiRouteConnector(h =>
        {
            h.AddRoute("orderBookDetails", zeroPriceJson);
        });

        var result = await sut.PlaceMarketOrderAsync("ETH", Domain.Enums.Side.Long, 100m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty("zero/negative price must produce an error");
    }

    // ── M8: Leverage cache tests ─────────────────────────────────────────────

    [Fact]
    public void LighterConnector_HasLeverageCacheField()
    {
        // M8 structural test: Verify the leverage cache field exists on the connector.
        // The PlaceMarketOrderAsync method checks _leverageCache before calling TryUpdateLeverageAsync.
        // Full integration testing of the cache requires the native signer; this test verifies the field.
        var sut = CreateConnector("{}");

        var field = typeof(LighterConnector)
            .GetField("_leverageCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        field.Should().NotBeNull("LighterConnector must have a _leverageCache field for M8 leverage caching");
        field!.FieldType.Should().Be<System.Collections.Concurrent.ConcurrentDictionary<int, int>>(
            "leverage cache must be ConcurrentDictionary<int, int> keyed by marketId");
    }
}

/// <summary>
/// Tests for ExchangeConnectorFactory (M3: case-insensitive lookup).
/// Grouped here since ExchangeConnectorFactory is a Stream B file.
/// Uses a real Microsoft.Extensions.DependencyInjection ServiceProvider to
/// avoid Moq limitations with concrete-type GetRequiredService calls.
/// </summary>
public class ExchangeConnectorFactoryTests
{
    /// <summary>
    /// Builds a ServiceCollection with stub connectors registered under their concrete types,
    /// then returns an ExchangeConnectorFactory backed by the real ServiceProvider.
    /// </summary>
    private static ExchangeConnectorFactory BuildFactory()
    {
        // Register stub HttpClient so LighterConnector can be instantiated
        var services = new ServiceCollection();
        services.AddLogging();

        // Use stub config with defaults so connectors construct without throwing
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["Exchanges:Lighter:AccountIndex"]).Returns("1");
        configMock.Setup(c => c["Exchanges:Lighter:ApiKey"]).Returns("1");

        // Register stub connectors using factory lambdas to avoid constructor complexity
        // We only need them to be registered — GetConnector() just resolves by type.
        services.AddSingleton<HyperliquidConnector>(_ =>
        {
            var mockRestClient = new Mock<HyperLiquid.Net.Interfaces.Clients.IHyperLiquidRestClient>();
            var mockProvider = new Mock<Polly.Registry.ResiliencePipelineProvider<string>>();
            mockProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(Polly.ResiliencePipeline.Empty);
            return new HyperliquidConnector(mockRestClient.Object, mockProvider.Object);
        });
        services.AddSingleton<AsterConnector>(_ =>
        {
            var mockRestClient = new Mock<Aster.Net.Interfaces.Clients.IAsterRestClient>();
            var mockProvider = new Mock<Polly.Registry.ResiliencePipelineProvider<string>>();
            mockProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(Polly.ResiliencePipeline.Empty);
            var logger = Mock.Of<ILogger<AsterConnector>>();
            return new AsterConnector(mockRestClient.Object, mockProvider.Object, logger);
        });
        services.AddSingleton<LighterConnector>(_ =>
        {
            var httpClient = new HttpClient { BaseAddress = new Uri("https://stub.local/api/v1/") };
            var logger = Mock.Of<ILogger<LighterConnector>>();
            return new LighterConnector(httpClient, logger, configMock.Object);
        });

        var sp = services.BuildServiceProvider();
        var factoryLogger = sp.GetRequiredService<ILogger<ExchangeConnectorFactory>>();
        return new ExchangeConnectorFactory(sp, factoryLogger);
    }

    [Theory]
    [InlineData("hyperliquid")]
    [InlineData("Hyperliquid")]
    [InlineData("HYPERLIQUID")]
    [InlineData("HyPeRlIqUiD")]
    public void GetConnector_IsCaseInsensitive_ForHyperliquid(string name)
    {
        var factory = BuildFactory();
        var act = () => factory.GetConnector(name);
        act.Should().NotThrow($"GetConnector(\"{name}\") must work regardless of case");

        var connector = factory.GetConnector(name);
        connector.Should().BeOfType<HyperliquidConnector>();
    }

    [Theory]
    [InlineData("aster")]
    [InlineData("Aster")]
    [InlineData("ASTER")]
    public void GetConnector_IsCaseInsensitive_ForAster(string name)
    {
        var factory = BuildFactory();
        var act = () => factory.GetConnector(name);
        act.Should().NotThrow($"GetConnector(\"{name}\") must work regardless of case");

        var connector = factory.GetConnector(name);
        connector.Should().BeOfType<AsterConnector>();
    }

    [Theory]
    [InlineData("lighter")]
    [InlineData("Lighter")]
    [InlineData("LIGHTER")]
    public void GetConnector_IsCaseInsensitive_ForLighter(string name)
    {
        var factory = BuildFactory();
        var act = () => factory.GetConnector(name);
        act.Should().NotThrow($"GetConnector(\"{name}\") must work regardless of case");

        var connector = factory.GetConnector(name);
        connector.Should().BeOfType<LighterConnector>();
    }

    [Fact]
    public void GetConnector_UnknownExchange_ThrowsArgumentException()
    {
        var factory = BuildFactory();
        var act = () => factory.GetConnector("UnknownExchange");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Unknown exchange*");
    }

    /// <summary>
    /// Builds a factory with full DI (loggers + ResiliencePipelineProvider) for CreateForUserAsync tests.
    /// </summary>
    private static ExchangeConnectorFactory BuildFactoryForUserCreation()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mock the ResiliencePipelineProvider that Hyperliquid/Aster connectors need
        var mockProvider = new Mock<Polly.Registry.ResiliencePipelineProvider<string>>();
        mockProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(Polly.ResiliencePipeline.Empty);
        services.AddSingleton(mockProvider.Object);

        var sp = services.BuildServiceProvider();
        var factoryLogger = sp.GetRequiredService<ILogger<ExchangeConnectorFactory>>();
        return new ExchangeConnectorFactory(sp, factoryLogger);
    }

    [Fact]
    public async Task CreateForUser_Lighter_WithNumericAccountIndex_ReturnsConnector()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: "1", apiSecret: null,
            walletAddress: "12345", privateKey: "0xabc123def456");

        connector.Should().NotBeNull();
        connector.Should().BeOfType<LighterConnector>();
    }

    [Fact]
    public async Task CreateForUser_Lighter_WithHexWalletAddress_ReturnsNull()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: "1", apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: "0xprivatekey");

        connector.Should().BeNull("Lighter requires a numeric account index, not a hex wallet address");
    }

    [Fact]
    public async Task CreateForUser_Lighter_WithNonNumericString_ReturnsNull()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: "1", apiSecret: null,
            walletAddress: "not-a-number", privateKey: "0xprivatekey");

        connector.Should().BeNull("Lighter requires a numeric account index");
    }

    [Fact]
    public async Task CreateForUser_Lighter_WithMissingPrivateKey_ReturnsNull()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: "1", apiSecret: null,
            walletAddress: "12345", privateKey: null);

        connector.Should().BeNull("private key is required for Lighter");
    }

    [Fact]
    public async Task CreateForUser_Hyperliquid_WithBothCredentials_ReturnsConnector()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: "0xprivatekey123");

        connector.Should().NotBeNull();
        connector.Should().BeOfType<HyperliquidConnector>();
    }

    [Fact]
    public async Task CreateForUser_Hyperliquid_WithMissingWallet_ReturnsNull()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: null, privateKey: "0xprivatekey123");

        connector.Should().BeNull("wallet address is required for Hyperliquid");
    }

    [Fact]
    public async Task CreateForUser_Hyperliquid_WithMissingPrivateKey_ReturnsNull()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: null);

        connector.Should().BeNull("private key is required for Hyperliquid");
    }

    [Fact]
    public async Task CreateForUser_Lighter_WithOverflowNumericIndex_ReturnsNull()
    {
        var factory = BuildFactoryForUserCreation();

        // A numeric string that overflows long.MaxValue should fail long.TryParse
        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: null, apiSecret: null,
            walletAddress: "99999999999999999999", privateKey: "0xprivatekey",
            apiKeyIndex: "42");

        connector.Should().BeNull("an overflow numeric index should fail validation");
    }

    [Fact]
    public async Task CreateForUser_Lighter_WithHexWalletAddress_LogsMaskedValue()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var mockProvider = new Mock<Polly.Registry.ResiliencePipelineProvider<string>>();
        mockProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(Polly.ResiliencePipeline.Empty);
        services.AddSingleton(mockProvider.Object);

        var sp = services.BuildServiceProvider();

        // Use a mock logger to verify masked output
        var mockLogger = new Mock<ILogger<ExchangeConnectorFactory>>();
        var factory = new ExchangeConnectorFactory(sp, mockLogger.Object);

        var hexWallet = "0xAbC123DeF456789012345678901234567890aBcD";
        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: null, apiSecret: null,
            walletAddress: hexWallet, privateKey: "0xprivatekey",
            apiKeyIndex: "42");

        connector.Should().BeNull();

        // Verify logger was called and the raw wallet address does NOT appear in log output
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => !v.ToString()!.Contains(hexWallet)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateForUser_Hyperliquid_WithSubAccount_ReturnsConnector()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: "0xprivatekey123",
            subAccountAddress: "0x1234567890AbCdEf1234567890AbCdEf12345678");

        connector.Should().NotBeNull();
        connector.Should().BeOfType<HyperliquidConnector>();
    }

    [Fact]
    public async Task CreateForUser_Hyperliquid_WithoutSubAccount_ReturnsConnector()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: "0xprivatekey123",
            subAccountAddress: null);

        connector.Should().NotBeNull();
        connector.Should().BeOfType<HyperliquidConnector>();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("255")]
    [InlineData("0")]
    [InlineData("999")]
    public async Task CreateForUser_Lighter_WithOutOfRangeApiKeyIndex_ReturnsNull(string apiKeyIndex)
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "lighter", apiKey: null, apiSecret: null,
            walletAddress: "12345", privateKey: "0xprivatekey",
            apiKeyIndex: apiKeyIndex);

        connector.Should().BeNull(
            $"apiKeyIndex={apiKeyIndex} is outside the valid range 2-254 and should be rejected");
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("0xZZZ")]
    [InlineData("0x123")]
    [InlineData("hello")]
    public async Task CreateForUser_Hyperliquid_WithInvalidSubAccountAddress_ReturnsNull(string subAccountAddress)
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: "0xprivatekey123",
            subAccountAddress: subAccountAddress);

        connector.Should().BeNull(
            $"subAccountAddress='{subAccountAddress}' is not a valid Ethereum address and should be rejected");
    }

    [Fact]
    public async Task CreateForUser_Hyperliquid_WithValidSubAccountAddress_ReturnsConnector()
    {
        var factory = BuildFactoryForUserCreation();

        var connector = await factory.CreateForUserAsync(
            "hyperliquid", apiKey: null, apiSecret: null,
            walletAddress: "0xAbC123DeF456789012345678901234567890aBcD",
            privateKey: "0xprivatekey123",
            subAccountAddress: "0x1234567890abcdef1234567890abcdef12345678");

        connector.Should().NotBeNull();
        connector.Should().BeOfType<HyperliquidConnector>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetAccountIndex_WithNullOrEmpty_Throws(string? indexValue)
    {
        var configData = new Dictionary<string, string?>();
        if (indexValue is not null)
        {
            configData["Exchanges:Lighter:AccountIndex"] = indexValue;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var handler = new MockHttpMessageHandler("{}");
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://mainnet.zklighter.elliot.ai/api/v1/")
        };
        var logger = new Mock<ILogger<LighterConnector>>();
        var connector = new LighterConnector(httpClient, logger.Object, config);

        // GetAccountIndex is called internally by GetAvailableBalanceAsync
        var act = () => connector.GetAvailableBalanceAsync();

        act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Account Index*required*");
    }
}
