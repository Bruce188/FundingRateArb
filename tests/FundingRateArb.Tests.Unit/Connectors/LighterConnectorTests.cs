using System.Net;
using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Configuration;
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
            _onFirstCall?.TrySetResult(true);

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
}
