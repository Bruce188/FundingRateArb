using System.Net;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Connectors;

public class DydxConnectorTests
{
    // Known test mnemonic for deterministic signer
    private const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ResiliencePipelineProvider<string> BuildEmptyPipelineProvider()
    {
        var mock = new Mock<ResiliencePipelineProvider<string>>();
        mock.Setup(p => p.GetPipeline(It.IsAny<string>()))
            .Returns(ResiliencePipeline.Empty);
        return mock.Object;
    }

    private static ILogger<DydxConnector> BuildNullLogger()
        => new Mock<ILogger<DydxConnector>>().Object;

    private static DydxSigner BuildTestSigner() => new(TestMnemonic);

    /// <summary>
    /// Creates a DydxConnector with mocked HTTP responses via MultiRouteHttpMessageHandler.
    /// </summary>
    private static DydxConnector BuildConnector(
        MultiRouteHttpMessageHandler? indexerHandler = null,
        MultiRouteHttpMessageHandler? validatorHandler = null,
        IMarkPriceCache? markPriceCache = null)
    {
        var ih = indexerHandler ?? new MultiRouteHttpMessageHandler();
        var vh = validatorHandler ?? new MultiRouteHttpMessageHandler();

        var indexerClient = new HttpClient(ih)
        {
            BaseAddress = new Uri("https://indexer.dydx.trade/v4/")
        };
        var validatorClient = new HttpClient(vh)
        {
            BaseAddress = new Uri("https://dydx-rpc.publicnode.com/")
        };
        var signer = BuildTestSigner();
        var cache = markPriceCache ?? new SingletonMarkPriceCache();

        return new DydxConnector(
            indexerClient, validatorClient, signer,
            BuildEmptyPipelineProvider(), BuildNullLogger(), cache);
    }

    private static string BuildPerpetualMarketsJson(
        string ticker = "BTC-USD",
        decimal oraclePrice = 50000m,
        decimal? nextFundingRate = 0.0001m,
        decimal initialMarginFraction = 0.05m,
        decimal stepSize = 0.0001m,
        decimal tickSize = 1m,
        int atomicResolution = -10,
        int quantumConversionExponent = -9,
        long stepBaseQuantums = 1000000,
        long subticksPerTick = 100000,
        int clobPairId = 0) => $$"""
        {
            "markets": {
                "{{ticker}}": {
                    "ticker": "{{ticker}}",
                    "status": "ACTIVE",
                    "oraclePrice": "{{oraclePrice}}",
                    "atomicResolution": {{atomicResolution}},
                    "quantumConversionExponent": {{quantumConversionExponent}},
                    "stepSize": "{{stepSize}}",
                    "tickSize": "{{tickSize}}",
                    "initialMarginFraction": "{{initialMarginFraction}}",
                    "stepBaseQuantums": {{stepBaseQuantums}},
                    "subticksPerTick": {{subticksPerTick}},
                    "clobPairId": "{{clobPairId}}",
                    "nextFundingRate": "{{nextFundingRate}}"
                }
            }
        }
        """;

    private static string BuildSubaccountJson(decimal freeCollateral = 1000m) => $$"""
        {
            "subaccount": {
                "address": "dydx1test",
                "subaccountNumber": 0,
                "equity": "{{freeCollateral + 500}}",
                "freeCollateral": "{{freeCollateral}}"
            }
        }
        """;

    private static string BuildPositionsJson(string ticker, string side, decimal size) => $$"""
        {
            "positions": [{
                "market": "{{ticker}}",
                "status": "OPEN",
                "side": "{{side}}",
                "size": "{{size}}",
                "entryPrice": "50000",
                "unrealizedPnl": "100",
                "realizedPnl": "0"
            }]
        }
        """;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFundingRates_ReturnsHourlyRate_NoNormalization()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(nextFundingRate: 0.0001m));

        using var connector = BuildConnector(indexerHandler: handler);
        var rates = await connector.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].RatePerHour.Should().Be(0.0001m);
        rates[0].RawRate.Should().Be(0.0001m);
    }

    [Fact]
    public async Task GetFundingRates_SymbolMapping_StripsDashUsd()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(ticker: "BTC-USD"));

        using var connector = BuildConnector(indexerHandler: handler);
        var rates = await connector.GetFundingRatesAsync();

        rates[0].Symbol.Should().Be("BTC");
        rates[0].ExchangeName.Should().Be("dYdX");
    }

    [Fact]
    public async Task GetFundingRates_EthSymbolMapping()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(ticker: "ETH-USD", oraclePrice: 3000m));

        using var connector = BuildConnector(indexerHandler: handler);
        var rates = await connector.GetFundingRatesAsync();

        rates[0].Symbol.Should().Be("ETH");
    }

    [Fact]
    public async Task GetFundingRates_InactiveMarket_IsExcluded()
    {
        var json = """
        {
            "markets": {
                "OLD-USD": {
                    "ticker": "OLD-USD",
                    "status": "OFFLINE",
                    "oraclePrice": "100",
                    "atomicResolution": -10,
                    "quantumConversionExponent": -9,
                    "stepSize": "0.01",
                    "tickSize": "0.1",
                    "initialMarginFraction": "0.1",
                    "stepBaseQuantums": 100000000,
                    "subticksPerTick": 1000000,
                    "clobPairId": "99",
                    "nextFundingRate": "0.0002"
                }
            }
        }
        """;
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", json);

        using var connector = BuildConnector(indexerHandler: handler);
        var rates = await connector.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAvailableBalance_ReturnsFreeCollateral()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("subaccountNumber/0", BuildSubaccountJson(freeCollateral: 5000m));

        using var connector = BuildConnector(indexerHandler: handler);
        var balance = await connector.GetAvailableBalanceAsync();

        balance.Should().Be(5000m);
    }

    [Fact]
    public async Task GetMaxLeverage_ComputesFromInitialMargin()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(initialMarginFraction: 0.05m));

        using var connector = BuildConnector(indexerHandler: handler);
        var maxLeverage = await connector.GetMaxLeverageAsync("BTC");

        maxLeverage.Should().Be(20); // 1 / 0.05 = 20
    }

    [Fact]
    public async Task GetMaxLeverage_InitialMargin10Pct_Returns10()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(initialMarginFraction: 0.10m));

        using var connector = BuildConnector(indexerHandler: handler);
        var maxLeverage = await connector.GetMaxLeverageAsync("BTC");

        maxLeverage.Should().Be(10);
    }

    [Fact]
    public async Task GetMarkPrice_ReturnsOraclePrice()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(oraclePrice: 62500m));

        using var connector = BuildConnector(indexerHandler: handler);
        var price = await connector.GetMarkPriceAsync("BTC");

        price.Should().Be(62500m);
    }

    [Fact]
    public async Task HasOpenPosition_Long_DetectsBySide()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualPositions", BuildPositionsJson("BTC-USD", "LONG", 0.5m));

        using var connector = BuildConnector(indexerHandler: handler);
        var hasPosition = await connector.HasOpenPositionAsync("BTC", Side.Long);

        hasPosition.Should().Be(true);
    }

    [Fact]
    public async Task HasOpenPosition_Short_DetectsBySide()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualPositions", BuildPositionsJson("BTC-USD", "SHORT", 0.5m));

        using var connector = BuildConnector(indexerHandler: handler);
        var hasPosition = await connector.HasOpenPositionAsync("BTC", Side.Short);

        hasPosition.Should().Be(true);
    }

    [Fact]
    public async Task HasOpenPosition_WrongSide_ReturnsFalse()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualPositions", BuildPositionsJson("BTC-USD", "LONG", 0.5m));

        using var connector = BuildConnector(indexerHandler: handler);
        var hasPosition = await connector.HasOpenPositionAsync("BTC", Side.Short);

        hasPosition.Should().Be(false);
    }

    [Fact]
    public async Task GetNextFundingTime_ReturnsNextHourBoundary()
    {
        using var connector = BuildConnector();
        var nextTime = await connector.GetNextFundingTimeAsync("BTC");

        nextTime.Should().NotBeNull();
        nextTime!.Value.Minute.Should().Be(0);
        nextTime.Value.Second.Should().Be(0);
        nextTime.Value.Should().BeAfter(DateTime.UtcNow);
        // Should be within the next 60 minutes
        (nextTime.Value - DateTime.UtcNow).TotalMinutes.Should().BeLessThanOrEqualTo(60);
    }

    [Fact]
    public void ExchangeName_IsDydx()
    {
        using var connector = BuildConnector();
        connector.ExchangeName.Should().Be("dYdX");
    }

    [Fact]
    public void IsEstimatedFillExchange_IsFalse()
    {
        using var connector = BuildConnector();
        connector.IsEstimatedFillExchange.Should().BeFalse();
    }

    [Fact]
    public void ToQuantums_ConvertsCorrectly()
    {
        // atomicResolution = -10 means 1 unit = 10^10 quantums
        var quantums = DydxConnector.ToQuantums(1.5m, -10);
        quantums.Should().Be(15_000_000_000UL);
    }

    [Fact]
    public void ToSubticks_ConvertsCorrectly()
    {
        // atomicResolution=-10, quantumConversionExponent=-9
        // subticks = price * 10^(atomicResolution - quantumConversionExponent) = price * 10^(-1)
        var subticks = DydxConnector.ToSubticks(50000m, -10, -9);
        subticks.Should().Be(5000UL);
    }

    [Fact]
    public async Task PlaceMarketOrderAsync_MarkPriceZero_ReturnsError()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(oraclePrice: 0m));

        using var connector = BuildConnector(indexerHandler: handler);
        var result = await connector.PlaceMarketOrderAsync("BTC", Side.Long, 100m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("zero or negative");
    }

    [Fact]
    public async Task GetQuantityPrecision_FromStepSize()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(stepSize: 0.0001m));

        using var connector = BuildConnector(indexerHandler: handler);
        var precision = await connector.GetQuantityPrecisionAsync("BTC");

        precision.Should().Be(4);
    }

    [Fact]
    public async Task GetQuantityPrecision_StepSize001_Returns2()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(stepSize: 0.01m));

        using var connector = BuildConnector(indexerHandler: handler);
        var precision = await connector.GetQuantityPrecisionAsync("BTC");

        precision.Should().Be(2);
    }

    [Fact]
    public async Task ClosePositionAsync_NoPosition_ReturnsError()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson());
        handler.AddRoute("perpetualPositions", """{ "positions": [] }""");

        using var connector = BuildConnector(indexerHandler: handler);
        var result = await connector.ClosePositionAsync("BTC", Side.Long);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No open");
    }
}
