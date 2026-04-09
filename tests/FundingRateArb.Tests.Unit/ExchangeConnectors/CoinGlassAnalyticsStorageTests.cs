using System.Net;
using System.Text;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Tests.Unit.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace FundingRateArb.Tests.Unit.ExchangeConnectors;

public class CoinGlassAnalyticsStorageTests
{
    private readonly Mock<ICoinGlassAnalyticsRepository> _mockAnalyticsRepo = new();
    private readonly Mock<HttpMessageHandler> _mockHandler = new();

    public CoinGlassAnalyticsStorageTests()
    {
        CoinGlassConnector.ResetBackoffState();
    }

    private CoinGlassConnector CreateConnector(string responseJson)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        var client = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("https://open-api-v3.coinglass.com/")
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ExchangeConnectors:CoinGlass:ApiKey"] = ""
            })
            .Build();

        return new CoinGlassConnector(
            client,
            config,
            NullLogger<CoinGlassConnector>.Instance,
            _mockAnalyticsRepo.Object,
            TestResiliencePipelineProvider.NoOp());
    }

    [Fact]
    public async Task GetFundingRates_StoresUnfilteredPerExchangeRates()
    {
        // Response includes Hyperliquid (DirectConnector) and Bybit (non-connector)
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "volume24hUsd": 50000000,
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 60000, "indexPrice": 60000, "intervalHours": 8 },
                        "Hyperliquid": { "rate": 0.00008, "markPrice": 60100, "indexPrice": 60000, "intervalHours": 1 }
                    }
                }
            ]
        }
        """;

        _mockAnalyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>());

        var connector = CreateConnector(json);
        var rates = await connector.GetFundingRatesAsync();

        // Trading rates should NOT include Hyperliquid (it's a DirectConnectorExchange)
        rates.Should().OnlyContain(r => r.ExchangeName == "CoinGlass");
        rates.Should().HaveCount(1);

        // Analytics storage should include BOTH Bybit AND Hyperliquid (unfiltered)
        _mockAnalyticsRepo.Verify(r => r.SaveSnapshotAsync(
            It.Is<List<CoinGlassExchangeRate>>(list =>
                list.Count == 2 &&
                list.Any(x => x.SourceExchange == "Bybit") &&
                list.Any(x => x.SourceExchange == "Hyperliquid")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFundingRates_DetectsNewExchange_CreatesDiscoveryEvent()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "ETHUSDT",
                    "volume24hUsd": 30000000,
                    "fundingRateByExchange": {
                        "Binance": { "rate": 0.0002, "markPrice": 3000, "indexPrice": 3000, "intervalHours": 8 },
                        "NewExchange": { "rate": 0.0001, "markPrice": 3000, "indexPrice": 3000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        // Only Binance+ETH is known; NewExchange is new
        _mockAnalyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>
            {
                ("Binance", "ETH")
            });

        var connector = CreateConnector(json);
        await connector.GetFundingRatesAsync();

        _mockAnalyticsRepo.Verify(r => r.SaveDiscoveryEventsAsync(
            It.Is<List<CoinGlassDiscoveryEvent>>(events =>
                events.Any(e => e.EventType == DiscoveryEventType.NewExchange &&
                                e.ExchangeName == "NewExchange")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFundingRates_DetectsNewCoin_CreatesDiscoveryEvent()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "volume24hUsd": 50000000,
                    "fundingRateByExchange": {
                        "Binance": { "rate": 0.0001, "markPrice": 60000, "indexPrice": 60000, "intervalHours": 8 }
                    }
                },
                {
                    "symbol": "SOLUSDT",
                    "volume24hUsd": 5000000,
                    "fundingRateByExchange": {
                        "Binance": { "rate": 0.00015, "markPrice": 150, "indexPrice": 150, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        // Only BTC on Binance is known; SOL on Binance is new
        _mockAnalyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>
            {
                ("Binance", "BTC")
            });

        var connector = CreateConnector(json);
        await connector.GetFundingRatesAsync();

        _mockAnalyticsRepo.Verify(r => r.SaveDiscoveryEventsAsync(
            It.Is<List<CoinGlassDiscoveryEvent>>(events =>
                events.Any(e => e.EventType == DiscoveryEventType.NewCoin &&
                                e.ExchangeName == "Binance" &&
                                e.Symbol == "SOL")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetFundingRates_AnalyticsFailure_DoesNotBlockRateReturn()
    {
        var json = """
        {
            "code": "0",
            "data": [
                {
                    "symbol": "BTCUSDT",
                    "volume24hUsd": 50000000,
                    "fundingRateByExchange": {
                        "Bybit": { "rate": 0.0001, "markPrice": 60000, "indexPrice": 60000, "intervalHours": 8 }
                    }
                }
            ]
        }
        """;

        _mockAnalyticsRepo.Setup(r => r.GetKnownPairsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(string Exchange, string Symbol)>());
        _mockAnalyticsRepo.Setup(r => r.SaveSnapshotAsync(
            It.IsAny<List<CoinGlassExchangeRate>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB connection failed"));

        var connector = CreateConnector(json);
        var rates = await connector.GetFundingRatesAsync();

        // Rates should still be returned despite analytics failure
        rates.Should().HaveCount(1);
        rates[0].Symbol.Should().Be("BTC");
    }
}
