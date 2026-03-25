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
    private static CoinGlassConnector CreateConnector(HttpResponseMessage response, string? apiKey = null)
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

        return new CoinGlassConnector(client, config, NullLogger<CoinGlassConnector>.Instance);
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
        rates.Should().OnlyContain(r => r.ExchangeName == "Binance" || r.ExchangeName == "Bybit");

        var btcBinance = rates.First(r => r.Symbol == "BTC" && r.ExchangeName == "Binance");
        btcBinance.RawRate.Should().Be(0.0001m);
        btcBinance.RatePerHour.Should().Be(0.0001m / 8); // 8-hour interval -> per-hour
        btcBinance.MarkPrice.Should().Be(65000m);
        btcBinance.Volume24hUsd.Should().Be(50000000m);
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
        rates[0].ExchangeName.Should().Be("Binance");
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

    [Fact]
    public void PlaceMarketOrderAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.PlaceMarketOrderAsync("BTC", Domain.Enums.Side.Long, 100m, 5);

        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void ClosePositionAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.ClosePositionAsync("BTC", Domain.Enums.Side.Long);

        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void GetAvailableBalanceAsync_ThrowsNotSupported()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        Func<Task> act = async () => await sut.GetAvailableBalanceAsync();

        act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void ExchangeName_ReturnsCoinGlass()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var sut = CreateConnector(response);

        sut.ExchangeName.Should().Be("CoinGlass");
    }
}
