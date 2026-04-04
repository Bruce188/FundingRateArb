using System.Net;
using System.Text;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace FundingRateArb.Tests.Unit.Connectors;

public class CoinGlassConnectorTests
{
    public CoinGlassConnectorTests()
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
        var connector = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, analyticsRepo.Object);
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
                        "dYdX": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 1 }
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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, CreateMockAnalyticsRepo());

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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, CreateMockAnalyticsRepo());

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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, CreateMockAnalyticsRepo());

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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, CreateMockAnalyticsRepo());
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
        var connector = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, CreateMockAnalyticsRepo());

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
        var connector = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance, CreateMockAnalyticsRepo());

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
}
