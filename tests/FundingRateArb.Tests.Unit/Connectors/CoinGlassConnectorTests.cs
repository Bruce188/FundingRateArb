using System.Net;
using System.Text;
using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace FundingRateArb.Tests.Unit.Connectors;

public class CoinGlassConnectorTests
{
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

        var connector = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance);
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
                        "Binance": { "rate": 0.0001, "markPrice": 65000, "indexPrice": 64990, "intervalHours": 8 },
                        "Bybit": { "rate": 0.00008, "markPrice": 65010, "indexPrice": 64995, "intervalHours": 8 }
                    }
                },
                {
                    "symbol": "ETHUSDT",
                    "volume24hUsd": 25000000,
                    "fundingRateByExchange": {
                        "Binance": { "rate": 0.00005, "markPrice": 3500, "indexPrice": 3499, "intervalHours": 8 }
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
                        "Binance": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
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

        // Only Binance should come through; Hyperliquid, Lighter, Aster are filtered
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
                        "Binance": { "rate": 0.0008, "markPrice": 65000, "intervalHours": 0 }
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
                        "Binance": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance);

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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance);

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
                        "Binance": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance);

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
                        "Binance": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
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

        // Only Binance should produce a rate; OKX with null value is silently skipped
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
                        "Binance": { "rate": 0.0008, "markPrice": 65000, "intervalHours": -1 }
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
                        "Binance": { "rate": 0.0001, "markPrice": 65000, "intervalHours": 8 }
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

        var sut = new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance);
        await sut.GetFundingRatesAsync();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("CG-API-KEY").Should().BeFalse();
    }
}
