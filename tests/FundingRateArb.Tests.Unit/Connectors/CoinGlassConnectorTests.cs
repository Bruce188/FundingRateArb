using System.Net;
using System.Text;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Tests.Unit.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace FundingRateArb.Tests.Unit.Connectors;

public class CoinGlassConnectorTests : IDisposable
{
    // NB6/NB7 fix (review-v133): CoinGlassConnector has static _backoffUntil /
    // _consecutiveFailures / _lastLoggedBackoffLevel. Reset between every test so
    // a prior failure case cannot short-circuit later tests via the manual backoff
    // path before the Polly pipeline runs.
    public CoinGlassConnectorTests()
    {
        CoinGlassConnector.ResetBackoffState();
    }

    public void Dispose()
    {
        CoinGlassConnector.ResetBackoffState();
    }

    private static ICoinGlassAnalyticsRepository CreateMockAnalyticsRepo()
    {
        var mock = new Mock<ICoinGlassAnalyticsRepository>();
        mock.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>());
        return mock.Object;
    }

    private static (CoinGlassConnector Connector, Mock<HttpMessageHandler> Handler) CreateConnectorWithHandler(
        HttpResponseMessage response, string? apiKey = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = apiKey ?? ""
            })
            .Build();

        var analyticsRepo = new Mock<ICoinGlassAnalyticsRepository>();
        analyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>());
        var connector = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            analyticsRepo.Object,
            TestResiliencePipelineProvider.NoOp());
        return (connector, handler);
    }

    private static CoinGlassConnector CreateConnector(HttpResponseMessage response, string? apiKey = null)
    {
        return CreateConnectorWithHandler(response, apiKey).Connector;
    }

    [Fact]
    public async Task GetFundingRatesAsync_ValidResponse_ReturnsRates()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "volume24hUsd": 50000000,
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "indexPrice": 64990, "intervalHours": 8 },
                        "OKX": { "rate": 0.00008, "markPrice": 65010, "indexPrice": 64995, "intervalHours": 8 }
                    }
                },
                {
                    "symbol": "ETHUSDT",
                    "volume24hUsd": 25000000,
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.00005, "markPrice": 3500, "indexPrice": 3499, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(3);
        // All rates are mapped to "CoinGlass" exchange entity for DB storage
        rates.Should().OnlyContain(r => r.ExchangeName == "CoinGlass");

        var btcFirst = rates.First(r => r.Symbol == "BTC");
        btcFirst.RawRate.Should().Be(0.0001m);
        btcFirst.RatePerHour.Should().Be(0.0001m / 8); // 8-hour interval -> per-hour
        btcFirst.MarkPrice.Should().Be(65000m);
        btcFirst.Volume24hUsd.Should().Be(50000000m);
    }

    [Fact]
    public async Task GetFundingRatesAsync_SkipsDirectConnectorExchanges()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "Hyperliquid": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 1 },
                        "Lighter": { "rate": 0.00008, "markPrice": 65000, "intervalHours": 1 },
                        "Aster": { "rate": 0.00007, "markPrice": 65000, "intervalHours": 8 },
                        "Binance": { "rate": 0.00006, "markPrice": 65000, "intervalHours": 8 },
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        // Only Bybit should come through; Hyperliquid, Lighter, Aster, Binance are filtered
        rates.Should().HaveCount(1);
        rates[0].ExchangeName.Should().Be("CoinGlass");
    }

    [Fact]
    public async Task GetFundingRatesAsync_NormalizesSymbols()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "ETHUSDT",
                    "fundingRateByExchange": {
                        "OKX": { "rate": 0.00005, "markPrice": 3500, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].Symbol.Should().Be("ETH"); // "ETHUSDT" -> "ETH"
    }

    [Fact]
    public async Task GetFundingRatesAsync_ApiFailure_ReturnsEmptyList()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFundingRatesAsync_InvalidJson_ReturnsEmptyList()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    // NB4: Fix un-awaited ThrowAsync assertions
    [Fact]
    public async Task PlaceMarketOrderAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.PlaceMarketOrderAsync("BTC", Domain.Enums.Side.Long, 100m, 5);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ClosePositionAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.ClosePositionAsync("BTC", Domain.Enums.Side.Long);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task GetAvailableBalanceAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.GetAvailableBalanceAsync();

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void ExchangeName_ReturnsCoinGlass()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        sut.ExchangeName.Should().Be("CoinGlass");
    }

    [Fact]
    public async Task GetMarkPriceAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.GetMarkPriceAsync("BTC");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    // NB11: intervalHours edge cases

    [Fact]
    public async Task GetFundingRatesAsync_ZeroIntervalHours_DefaultsToEight()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0008, "markPrice": 65000, "intervalHours": 0 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].RatePerHour.Should().Be(0.0008m / 8); // 0 defaults to 8
    }

    [Fact]
    public async Task GetFundingRatesAsync_NonStandardInterval_CalculatesCorrectly()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 1 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].RatePerHour.Should().Be(0.0001m / 1); // 1-hour interval
    }

    // NB12: NormalizeSymbol coverage via GetFundingRatesAsync

    [Theory]
    [InlineData("BTCUSDT", "BTC")]
    [InlineData("ETH-PERP", "ETH")]
    [InlineData("SOL_PERP", "SOL")]
    [InlineData("BTC/USD", "BTC")]
    [InlineData("BTCUSD", "BTC")]
    [InlineData("DOGE/USDT", "DOGE")]
    [InlineData("BTC", "BTC")]       // NB5: no suffix to strip
    [InlineData("LINK-", "LINK")]     // NB5: trailing separator
    [InlineData("LINKUSD", "LINK")]   // NB5: matches "USD" specifically
    [InlineData("SOLUSD_PERP", "SOL")]  // NB1: compound suffix (USD + _PERP) must strip fully
    public async Task GetFundingRatesAsync_NormalizeSymbol_AllPatterns(string inputSymbol, string expectedSymbol)
    {
        var json = $$"""
        {
            "code": "0",
            "data": [
                {
                    "symbol": "{{inputSymbol}}",
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].Symbol.Should().Be(expectedSymbol);
    }

    // NB13: API key header test

    [Fact]
    public async Task GetFundingRatesAsync_WithApiKey_SendsHeader()
    {
        var json = """
        {
            "code": "0",
            "data": []
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpRequestMessage? capturedRequest = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(response);

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key-123"
            })
            .Build();

        var sut = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            CreateMockAnalyticsRepo(),
            TestResiliencePipelineProvider.NoOp());

        await sut.GetFundingRatesAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("CG-API-KEY").Should().BeTrue();
        capturedRequest.Headers.GetValues("CG-API-KEY").Should().Contain("test-key-123");
    }

    // N8: HttpRequestException test

    [Fact]
    public async Task GetFundingRatesAsync_HttpException_ReturnsEmptyList()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network timeout"));

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = ""
            })
            .Build();

        var sut = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            CreateMockAnalyticsRepo(),
            TestResiliencePipelineProvider.NoOp());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    // N9: Non-zero error code test

    [Fact]
    public async Task GetFundingRatesAsync_NonZeroErrorCode_ReturnsEmptyList()
    {
        var json = """
        {
            "code": "40001",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        // Should return empty despite data being present because code != "0"
        rates.Should().BeEmpty();
    }

    // NB6: Timeout/TaskCanceledException test

    [Fact]
    public async Task GetFundingRatesAsync_Timeout_ReturnsEmptyList()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = ""
            })
            .Build();

        var sut = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            CreateMockAnalyticsRepo(),
            TestResiliencePipelineProvider.NoOp());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    // NB6: Null exchange value in FundingRateByExchange dictionary

    [Fact]
    public async Task GetFundingRatesAsync_NullExchangeValue_SkipsNullEntry()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "OKX": null,
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        // Only Bybit should produce a rate; OKX with null value is silently skipped
        rates.Should().HaveCount(1);
        rates[0].RawRate.Should().Be(0.0001m);
    }

    // N9: Negative intervalHours defaults to 8

    [Fact]
    public async Task GetFundingRatesAsync_NegativeIntervalHours_DefaultsToEight()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0008, "markPrice": 65000, "intervalHours": -1 }
                    }
                }
            ]
        }
        """;

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var sut = CreateConnector(response);
        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].RatePerHour.Should().Be(0.0008m / 8); // negative defaults to 8
    }

    // N6: API key header absence test

    [Fact]
    public async Task GetFundingRatesAsync_WithoutApiKey_DoesNotSendHeader()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        HttpRequestMessage? capturedRequest = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = ""
            })
            .Build();

        var sut = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            CreateMockAnalyticsRepo(),
            TestResiliencePipelineProvider.NoOp());
        await sut.GetFundingRatesAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("CG-API-KEY").Should().BeFalse();
    }

    // ── Backoff circuit breaker tests ─────────────────────────────────────────

    [Fact]
    public async Task GetFundingRatesAsync_FirstFailure_BacksOff60Seconds()
    {
        var errorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var (connector, handler) = CreateConnectorWithHandler(errorResponse);

        // First call — fails, sets backoff
        var result1 = await connector.GetFundingRatesAsync();
        result1.Should().BeEmpty();

        // Second call — should be blocked by backoff (returns [] without API call)
        var result2 = await connector.GetFundingRatesAsync();
        result2.Should().BeEmpty();

        // Verify only 1 HTTP call was made (second was blocked by backoff)
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetFundingRatesAsync_SuccessAfterFailure_ResetsBackoff()
    {
        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                // Second call succeeds with valid data
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "code": "0",
                        "data": [
                            {
                                "symbol": "BTCUSDT",
                                "fundingRateByExchange": {
                                    "Bybit": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
                                }
                            }
                        ]
                    }
                    """, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = ""
            })
            .Build();
        var connector = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            CreateMockAnalyticsRepo(),
            TestResiliencePipelineProvider.NoOp());

        // First call fails — sets backoff
        await connector.GetFundingRatesAsync();

        // Manually call a third time after the backoff would expire
        // Since we can't easily time-travel, we test the reset on success by
        // using reflection to clear the backoff, simulating time passage
        var backoffField = typeof(CoinGlassConnector).GetField("_backoffUntil",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        backoffField.Should().NotBeNull("_backoffUntil field must exist for backoff testing");
        backoffField!.SetValue(connector, DateTime.MinValue);

        // Second call succeeds — should reset failure counter
        var result2 = await connector.GetFundingRatesAsync();
        result2.Should().NotBeEmpty();

        // Set backoff to past again to simulate time passage
        backoffField!.SetValue(connector, DateTime.MinValue);

        // Third call should succeed (backoff was reset by successful call)
        var result3 = await connector.GetFundingRatesAsync();
        result3.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetFundingRatesAsync_DuringBackoff_ReturnsEmptyWithoutApiCall()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = ""
            })
            .Build();
        var connector = new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            CreateMockAnalyticsRepo(),
            TestResiliencePipelineProvider.NoOp());

        // First call — triggers backoff
        await connector.GetFundingRatesAsync();

        // Second call — should be in backoff, no API call
        var result = await connector.GetFundingRatesAsync();
        result.Should().BeEmpty();

        // Only 1 API call should have been made
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    // ── Review-v113: NB9 — Backoff duration boundary tests ──────────

    [Fact]
    public async Task GetFundingRatesAsync_FirstFailure_BackoffIsApproximately60Seconds()
    {
        var errorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var (connector, _) = CreateConnectorWithHandler(errorResponse);

        var before = DateTime.UtcNow;
        await connector.GetFundingRatesAsync();
        var after = DateTime.UtcNow;

        var backoffField = typeof(CoinGlassConnector).GetField("_backoffUntil",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        backoffField.Should().NotBeNull();
        var backoffUntil = (DateTime)backoffField!.GetValue(null)!;

        // First failure: backoff = 60 * 2^0 = 60 seconds
        var backoffDuration = backoffUntil - before;
        backoffDuration.TotalSeconds.Should().BeInRange(58, 65, "first failure should backoff ~60 seconds");
    }

    [Fact]
    public async Task GetFundingRatesAsync_ManyFailures_BackoffCappedAt900Seconds()
    {
        var errorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var (connector, _) = CreateConnectorWithHandler(errorResponse);

        var backoffField = typeof(CoinGlassConnector).GetField("_backoffUntil",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        backoffField.Should().NotBeNull();

        // Simulate 10 consecutive failures by resetting backoff between each call
        for (int i = 0; i < 10; i++)
        {
            backoffField!.SetValue(connector, DateTime.MinValue);
            await connector.GetFundingRatesAsync();
        }

        var before = DateTime.UtcNow;
        var backoffUntil = (DateTime)backoffField!.GetValue(null)!;
        var backoffDuration = backoffUntil - before;

        // Cap is 900 seconds (15 minutes)
        backoffDuration.TotalSeconds.Should().BeLessOrEqualTo(905, "backoff should be capped at 900 seconds");
        backoffDuration.TotalSeconds.Should().BeGreaterOrEqualTo(895, "after many failures backoff should approach cap");
    }

    // ── plan-v61 Task 2.1: body logging, IsAvailable, Polly circuit breaker ──

    private static (CoinGlassConnector Connector, Mock<HttpMessageHandler> Handler, ListLogger<CoinGlassConnector> Logger)
        CreateConnectorWithLogger(
            HttpResponseMessage response,
            Polly.Registry.ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        return CreateWithHandlerMock(handler, pipelineProvider);
    }

    private static (CoinGlassConnector Connector, Mock<HttpMessageHandler> Handler, ListLogger<CoinGlassConnector> Logger)
        CreateWithHandlerMock(
            Mock<HttpMessageHandler> handler,
            Polly.Registry.ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        var client = new HttpClient(handler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = "test-key",
            })
            .Build();

        var analyticsRepo = new Mock<ICoinGlassAnalyticsRepository>();
        analyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>());

        var logger = new ListLogger<CoinGlassConnector>();
        var connector = new CoinGlassConnector(
            client,
            config,
            logger,
            analyticsRepo.Object,
            pipelineProvider ?? TestResiliencePipelineProvider.NoOp());
        return (connector, handler, logger);
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CoinGlassConnector_Returns401_LogsBodyAndReturnsUnavailable()
    {
        var response = ErrorResponse(HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
        var (connector, _, logger) = CreateConnectorWithLogger(response);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("401", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("unauthorized", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassConnector_Returns429_LogsBodyAndReturnsUnavailable()
    {
        var response = ErrorResponse(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limited\"}");
        var (connector, _, logger) = CreateConnectorWithLogger(response);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("429", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("rate limited", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassConnector_Returns500_LogsBodyAndReturnsUnavailable()
    {
        var response = ErrorResponse(HttpStatusCode.InternalServerError, "{\"error\":\"internal\"}");
        var (connector, _, logger) = CreateConnectorWithLogger(response);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("500", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("internal", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassConnector_Timeout_LogsAndReturnsUnavailable()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("request timeout after 30s"));

        var (connector, _, logger) = CreateWithHandlerMock(handler);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("CoinGlass API request failed", LogLevel.Warning).Should().BeTrue();
        // NB9 fix (review-v133): assert the sanitized exception message reaches the log,
        // not just the static template prefix. Regressions that dropped the {Body}
        // placeholder would leave the prefix intact but hide the timeout detail.
        logger.ContainsMessage("request timeout after 30s", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassConnector_MalformedJson_LogsBodyAndReturnsUnavailable()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{invalid json", Encoding.UTF8, "application/json"),
        };
        var (connector, _, logger) = CreateConnectorWithLogger(response);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("Failed to parse CoinGlass response", LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public async Task CoinGlassConnector_CircuitOpens_After5ConsecutiveFailures()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => ErrorResponse(HttpStatusCode.InternalServerError, "oops"));

        var (connector, handlerMock, _) = CreateWithHandlerMock(handler, provider);

        // NB6 fix (review-v133): use ResetBackoffState() instead of reflection. The manual
        // backoff would otherwise early-return before the Polly pipeline runs.
        int maxAttempts = 10;
        for (int i = 0; i < maxAttempts && connector.IsAvailable; i++)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }

        connector.IsAvailable.Should().BeFalse(
            "circuit breaker should have opened after enough consecutive failures (>= 5)");

        var sendCountAfterOpen = handlerMock.Invocations.Count;

        // Subsequent calls must not hit the handler at all — reset manual backoff before each
        // probe so this test verifies the *Polly* circuit breaker's short-circuit semantics.
        for (int i = 0; i < 3; i++)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
        }

        handlerMock.Invocations.Count.Should().Be(sendCountAfterOpen,
            "open circuit should short-circuit all subsequent calls without hitting SendAsync");
    }

    [Fact]
    public async Task CoinGlassConnector_CircuitHalfOpens_After5Minutes()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return ErrorResponse(HttpStatusCode.InternalServerError, "fail");
            });

        var (connector, handlerMock, _) = CreateWithHandlerMock(handler, provider);

        // NB6 fix: ResetBackoffState() instead of reflection.
        for (int i = 0; i < 10 && connector.IsAvailable; i++)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        connector.IsAvailable.Should().BeFalse();

        var failuresBeforeHalfOpen = callCount;

        // Advance past the 5-minute break duration — the circuit should transition to
        // half-open and allow the next call through to the handler.
        fakeTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        CoinGlassConnector.ResetBackoffState();
        await connector.GetFundingRatesAsync();

        callCount.Should().BeGreaterThan(failuresBeforeHalfOpen,
            "after the break window the pipeline should allow one probe through");
    }

    [Fact]
    public async Task CoinGlassConnector_CircuitCloses_OnFirstSuccessAfterHalfOpen()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount <= 5)
                {
                    return ErrorResponse(HttpStatusCode.InternalServerError, "fail");
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":"0","data":[]}""", Encoding.UTF8, "application/json"),
                };
            });

        var (connector, _, _) = CreateWithHandlerMock(handler, provider);

        // NB6 fix: ResetBackoffState() instead of reflection.
        for (int i = 0; i < 10 && connector.IsAvailable; i++)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        connector.IsAvailable.Should().BeFalse();

        // Advance past break window and make a successful probe
        fakeTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        CoinGlassConnector.ResetBackoffState();
        await connector.GetFundingRatesAsync();

        connector.IsAvailable.Should().BeTrue("first successful call after half-open should close the circuit");
    }

    [Fact]
    public async Task CoinGlassConnector_BodyLogging_RedactsCgApiKey()
    {
        var response = ErrorResponse(
            HttpStatusCode.Unauthorized,
            "CG-API-KEY: secret-key-value\nerror: invalid key");
        var (connector, _, logger) = CreateConnectorWithLogger(response);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        var warningEntries = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warningEntries.Should().NotBeEmpty();
        warningEntries.Should().NotContain(e => e.Message.Contains("secret-key-value", StringComparison.Ordinal));
        warningEntries.Should().Contain(e => e.Message.Contains("REDACTED", StringComparison.Ordinal));
    }

    // ── review-v133 Task 2.1 reopen tests ──

    /// <summary>
    /// B1 fix: after the breaker opens and Polly's 5-minute break elapses, the next call
    /// must reach the HttpMessageHandler (the half-open probe). Before the fix, every
    /// short-circuited call re-invoked RecordFailure() which pushed _backoffUntil further
    /// into the future, so the manual-backoff guard blocked the probe at line 96 before
    /// the Polly pipeline ever ran.
    /// </summary>
    [Fact]
    public async Task CoinGlassConnector_CircuitBrokenThenHalfOpen_DoesNotStackManualBackoff()
    {
        CoinGlassConnector.ResetBackoffState();

        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref callCount);
                return ErrorResponse(HttpStatusCode.InternalServerError, "fail");
            });

        var (connector, _, _) = CreateWithHandlerMock(handler, provider);

        // Drive the breaker open (reset manual backoff between iterations so the Polly
        // pipeline actually runs each time).
        for (int i = 0; i < 10 && connector.IsAvailable; i++)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        connector.IsAvailable.Should().BeFalse();

        // Fire several short-circuited calls — each goes through catch (BrokenCircuitException).
        // Pre-fix, each one called RecordFailure() which extended _backoffUntil.
        for (int i = 0; i < 5; i++)
        {
            await connector.GetFundingRatesAsync();
        }

        var invocationsBeforeProbe = callCount;

        // Advance past the 5-minute break duration. Do NOT reset backoff — we want to
        // verify the fix ensures _backoffUntil has not drifted past "now" because of
        // the stacked RecordFailure() calls inside the short-circuit catch block.
        fakeTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(5));

        await connector.GetFundingRatesAsync();

        callCount.Should().BeGreaterThan(invocationsBeforeProbe,
            "after the Polly break elapses the next call must reach the handler (the " +
            "half-open probe) — a stacked manual backoff would block it first");
    }

    /// <summary>
    /// B1 fix: BrokenCircuitException must NOT increment _consecutiveFailures, because
    /// a short-circuit is the breaker doing its job, not a new upstream failure.
    /// </summary>
    [Fact]
    public async Task CoinGlassConnector_BrokenCircuitException_DoesNotIncrementConsecutiveFailures()
    {
        CoinGlassConnector.ResetBackoffState();

        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => ErrorResponse(HttpStatusCode.InternalServerError, "oops"));

        var (connector, _, _) = CreateWithHandlerMock(handler, provider);

        // Drive the breaker open
        for (int i = 0; i < 10 && connector.IsAvailable; i++)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
        }
        connector.IsAvailable.Should().BeFalse();

        // Reset to a known baseline (_consecutiveFailures = 0) then fire more calls —
        // all of which will short-circuit. Pre-fix, each incremented the counter.
        CoinGlassConnector.ResetBackoffState();

        var failuresField = typeof(CoinGlassConnector).GetField("_consecutiveFailures",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        for (int i = 0; i < 5; i++)
        {
            await connector.GetFundingRatesAsync();
        }

        var failuresAfterShortCircuits = (int)failuresField.GetValue(null)!;
        failuresAfterShortCircuits.Should().Be(0,
            "short-circuited calls must not increment _consecutiveFailures — the breaker " +
            "is already doing its job, and stacking on top of it blocks recovery");
    }

    /// <summary>
    /// NB1 fix: inner exception messages (which may carry the API key in custom handlers
    /// or logging middleware) must be sanitized. Before the fix the logger was passed
    /// the raw `ex` parameter and the log sink serialized `ex.ToString()` verbatim,
    /// leaking the inner message.
    /// </summary>
    [Fact]
    public async Task CoinGlassConnector_InnerExceptionWithSecret_NotLogged()
    {
        var inner = new InvalidOperationException("wrapped CG-API-KEY: innersecret123 trailing");
        var outer = new HttpRequestException("request failed", inner);

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(outer);

        var (connector, _, logger) = CreateWithHandlerMock(handler);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().NotBeEmpty();
        warnings.Should().NotContain(e => e.Message.Contains("innersecret123", StringComparison.Ordinal),
            "the inner exception message must be sanitized before logging");
        // Also confirm the raw Exception object was NOT passed to the logger (so the sink
        // cannot dump ex.ToString() itself): all warning entries for this call should have
        // a null Exception column.
        warnings.Where(e => e.Message.Contains("CoinGlass API request failed", StringComparison.Ordinal))
            .Should().OnlyContain(e => e.Exception == null,
                "the exception object must not be passed verbatim — only the sanitized string form");
    }

    /// <summary>
    /// NB4 fix: JSON parse failures on non-seekable response streams used to log an empty
    /// body because ReadFromJsonAsync consumed the stream before ReadAsStringAsync could
    /// read it. After the fix the body is read as a string first, then deserialized,
    /// so the malformed text is available for logging.
    /// </summary>
    [Fact]
    public async Task CoinGlassConnector_MalformedJsonOnNonSeekableStream_BodyAppearsInLog()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ForwardOnlyStreamContent("{invalid json"),
        };
        var (connector, _, logger) = CreateConnectorWithLogger(response);

        var result = await connector.GetFundingRatesAsync();

        result.Should().BeEmpty();
        logger.ContainsMessage("Failed to parse CoinGlass response", LogLevel.Warning).Should().BeTrue();
        logger.ContainsMessage("invalid", LogLevel.Warning).Should().BeTrue(
            "the malformed body text must appear in the log — pre-fix it was empty because " +
            "ReadFromJsonAsync had already consumed the non-seekable stream");
    }

    /// <summary>
    /// NB8 fix: pin the 5-failure threshold. Counts handler invocations before
    /// IsAvailable flips to false to guard against a regression that bumped MinimumThroughput.
    /// </summary>
    [Fact]
    public async Task CoinGlassConnector_CircuitOpens_AtExactly5Failures()
    {
        CoinGlassConnector.ResetBackoffState();

        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var provider = TestResiliencePipelineProvider.WithCircuitBreaker(fakeTime);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref callCount);
                return ErrorResponse(HttpStatusCode.InternalServerError, "fail");
            });

        var (connector, _, _) = CreateWithHandlerMock(handler, provider);

        int attempts = 0;
        while (connector.IsAvailable && attempts < 20)
        {
            CoinGlassConnector.ResetBackoffState();
            await connector.GetFundingRatesAsync();
            fakeTime.Advance(TimeSpan.FromMilliseconds(100));
            attempts++;
        }

        connector.IsAvailable.Should().BeFalse();
        callCount.Should().BeInRange(5, 6,
            "plan-v61 specifies 5 failures trip the breaker with Polly's rolling window " +
            "(may need 1 extra sample before the sliding window has enough data)");
    }

    /// <summary>
    /// Minimal HttpContent that exposes the body as a forward-only (non-seekable) stream,
    /// mirroring the behavior of a real network response and ensuring NB4's fix is
    /// exercised end-to-end.
    /// </summary>
    private sealed class ForwardOnlyStreamContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ForwardOnlyStreamContent(string body)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
