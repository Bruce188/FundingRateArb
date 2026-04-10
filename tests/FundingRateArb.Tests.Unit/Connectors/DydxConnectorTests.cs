using System.Net;
using System.Text.Json;
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
        IMarkPriceCache? markPriceCache = null,
        bool withSigner = true)
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
        var signer = withSigner ? BuildTestSigner() : null;
        var cache = markPriceCache ?? new SingletonMarkPriceCache();

        return new DydxConnector(
            indexerClient, validatorClient, signer,
            BuildEmptyPipelineProvider(), BuildNullLogger(), cache);
    }

    private static string BuildAccountInfoJson(ulong accountNumber = 0, ulong sequence = 0) => $$"""
        {
            "account": {
                "account_number": "{{accountNumber}}",
                "sequence": "{{sequence}}"
            }
        }
        """;

    private static string BuildBroadcastResponseJson(string txHash = "AABBCC", int code = 0) => $$"""
        {
            "tx_response": {
                "code": {{code}},
                "txhash": "{{txHash}}",
                "raw_log": ""
            }
        }
        """;

    private static string BuildHeightJson(uint height = 1000) => $$"""
        {
            "height": "{{height}}",
            "time": "2026-01-01T00:00:00Z"
        }
        """;

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
    public async Task GetFundingRatesAsync_ReturnsNextSettlementUtc_AtNextHourBoundary()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(nextFundingRate: 0.0001m));

        using var connector = BuildConnector(indexerHandler: handler);

        var before = DateTime.UtcNow;

        var rates = await connector.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        var dto = rates[0];
        dto.NextSettlementUtc.Should().HaveValue();
        dto.NextSettlementUtc!.Value.Should().BeAfter(before, "NextSettlementUtc must be in the future");
        dto.NextSettlementUtc.Value.Minute.Should().Be(0, "NextSettlementUtc must be on an hour boundary");
        dto.NextSettlementUtc.Value.Second.Should().Be(0, "NextSettlementUtc must be on an hour boundary");
        dto.NextSettlementUtc.Value.Kind.Should().Be(DateTimeKind.Utc, "settlement times must be UTC");
        (dto.NextSettlementUtc.Value - DateTime.UtcNow).TotalMinutes.Should().BeInRange(0, 60, "NextSettlementUtc must be no more than one hour away");
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

    // ── B3: PlaceMarketOrderByQuantityAsync tests ──────────────────────────────

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_MarkPriceZero_ReturnsError()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(oraclePrice: 0m));

        using var connector = BuildConnector(indexerHandler: handler);
        var result = await connector.PlaceMarketOrderByQuantityAsync("BTC", Side.Long, 0.5m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("zero or negative");
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_RoundedQuantityZero_ReturnsError()
    {
        var handler = new MultiRouteHttpMessageHandler();
        // stepSize = 1.0 means quantities are rounded to whole numbers; 0.0001 rounds to 0
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(stepSize: 1m, oraclePrice: 50000m));

        using var connector = BuildConnector(indexerHandler: handler);
        var result = await connector.PlaceMarketOrderByQuantityAsync("BTC", Side.Long, 0.0001m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Rounded quantity is zero");
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_MarketInfoNotFound_ReturnsError()
    {
        // Return markets JSON that contains only ETH-USD, not BTC-USD
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(ticker: "ETH-USD", oraclePrice: 3000m));

        // Mock mark price cache to return a price for BTC so the mark-price check passes
        var mockCache = new Mock<IMarkPriceCache>();
        mockCache.Setup(c => c.GetOrRefreshAsync("dYdX", "BTC-USD", It.IsAny<Func<CancellationToken, Task<Dictionary<string, decimal>>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(50000m);

        using var connector = BuildConnector(indexerHandler: handler, markPriceCache: mockCache.Object);
        var result = await connector.PlaceMarketOrderByQuantityAsync("BTC", Side.Long, 0.5m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Market info not found");
    }

    // ── B4: ClosePositionAsync success tests ────────────────────────────────────

    [Fact]
    public async Task ClosePositionAsync_LongPosition_Success()
    {
        var indexerHandler = new MultiRouteHttpMessageHandler();
        indexerHandler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson());
        indexerHandler.AddRoute("perpetualPositions", BuildPositionsJson("BTC-USD", "LONG", 0.5m));
        indexerHandler.AddRoute("height", BuildHeightJson());

        var validatorHandler = new MultiRouteHttpMessageHandler();
        validatorHandler.AddRoute("accounts", BuildAccountInfoJson());
        validatorHandler.AddRoute("txs", BuildBroadcastResponseJson());

        using var connector = BuildConnector(indexerHandler: indexerHandler, validatorHandler: validatorHandler);
        var result = await connector.ClosePositionAsync("BTC", Side.Long);

        result.Success.Should().BeTrue();
        result.FilledQuantity.Should().Be(0.5m);
    }

    [Fact]
    public async Task ClosePositionAsync_ShortPosition_Success()
    {
        var indexerHandler = new MultiRouteHttpMessageHandler();
        indexerHandler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson());
        indexerHandler.AddRoute("perpetualPositions", BuildPositionsJson("BTC-USD", "SHORT", 0.3m));
        indexerHandler.AddRoute("height", BuildHeightJson());

        var validatorHandler = new MultiRouteHttpMessageHandler();
        validatorHandler.AddRoute("accounts", BuildAccountInfoJson());
        validatorHandler.AddRoute("txs", BuildBroadcastResponseJson());

        using var connector = BuildConnector(indexerHandler: indexerHandler, validatorHandler: validatorHandler);
        var result = await connector.ClosePositionAsync("BTC", Side.Short);

        result.Success.Should().BeTrue();
        result.FilledQuantity.Should().Be(0.3m);
    }

    // ── B5: Parameterized ToQuantums/ToSubticks tests ───────────────────────────

    [Theory]
    [InlineData(1.5, -10, 15_000_000_000UL)]
    [InlineData(1.0, -6, 1_000_000UL)]
    [InlineData(0.001, -8, 100_000UL)]
    [InlineData(2.5, -10, 25_000_000_000UL)]
    [InlineData(0.1, -6, 100_000UL)]
    public void ToQuantums_VariousResolutions_ConvertsCorrectly(
        double quantity, int atomicResolution, ulong expected)
    {
        var quantums = DydxConnector.ToQuantums((decimal)quantity, atomicResolution);
        quantums.Should().Be(expected);
    }

    [Theory]
    [InlineData(50000, -10, -9, 5000UL)]
    [InlineData(50000, -6, -9, 50_000_000UL)]
    [InlineData(3000, -10, -9, 300UL)]
    [InlineData(1.5, -8, -9, 15UL)]
    public void ToSubticks_VariousResolutions_ConvertsCorrectly(
        double price, int atomicResolution, int quantumConversionExponent, ulong expected)
    {
        var subticks = DydxConnector.ToSubticks((decimal)price, atomicResolution, quantumConversionExponent);
        subticks.Should().Be(expected);
    }

    // ── NB8: Null signer guard test ────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrderAsync_NullSigner_ThrowsInvalidOperationException()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson());

        using var connector = BuildConnector(indexerHandler: handler, withSigner: false);

        var act = () => connector.PlaceMarketOrderAsync("BTC", Side.Long, 100m, 5);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*signing credentials*");
    }

    // ── NB9: GetMaxLeverage with zero initial margin ───────────────────────────

    [Fact]
    public async Task GetMaxLeverage_InitialMarginFractionZero_ReturnsNull()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", BuildPerpetualMarketsJson(initialMarginFraction: 0m));

        using var connector = BuildConnector(indexerHandler: handler);
        var maxLeverage = await connector.GetMaxLeverageAsync("BTC");

        maxLeverage.Should().BeNull();
    }

    // ── NB10: HasOpenPosition exception path ───────────────────────────────────

    [Fact]
    public async Task HasOpenPosition_WhenApiThrows_ReturnsNull()
    {
        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualPositions", "server error", System.Net.HttpStatusCode.InternalServerError);

        using var connector = BuildConnector(indexerHandler: handler);
        var result = await connector.HasOpenPositionAsync("BTC", Side.Long);

        result.Should().BeNull();
    }

    // ── Task 4.1: StringDecimalConverter null-oracle handling ──────────────────

    private static readonly JsonSerializerOptions StringDecimalConverterOptions = new()
    {
        Converters = { new StringDecimalConverter() }
    };

    [Fact]
    public void StringDecimalConverter_Null_ReturnsZero()
    {
        // dYdX returns null oraclePrice for inactive markets (e.g. WTI-USD outside trading hours).
        // The converter must normalize null to 0m rather than throwing InvalidOperationException.
        var result = JsonSerializer.Deserialize<decimal>("null", StringDecimalConverterOptions);

        result.Should().Be(0m);
    }

    [Fact]
    public void DydxPerpetualMarketsResponse_Deserializes_WithNullOraclePrice()
    {
        // Full response fixture containing an inactive market with `oraclePrice: null`.
        // The response must parse successfully and the inactive market's OraclePrice must be 0m.
        var json = """
        {
            "markets": {
                "WTI-USD": {
                    "ticker": "WTI-USD",
                    "status": "INACTIVE",
                    "oraclePrice": null,
                    "atomicResolution": -10,
                    "quantumConversionExponent": -9,
                    "stepSize": "0.01",
                    "tickSize": "0.1",
                    "initialMarginFraction": "0.1",
                    "stepBaseQuantums": 100000000,
                    "subticksPerTick": 1000000,
                    "clobPairId": "42",
                    "nextFundingRate": "0"
                }
            }
        }
        """;

        var response = JsonSerializer.Deserialize<DydxPerpetualMarketsResponse>(json);

        response.Should().NotBeNull();
        response!.Markets.Should().ContainKey("WTI-USD");
        response.Markets["WTI-USD"].OraclePrice.Should().Be(0m);
        response.Markets["WTI-USD"].Status.Should().Be("INACTIVE");
    }

    [Fact]
    public async Task DydxConnector_SkipsMarkets_WithNullOraclePrice()
    {
        // End-to-end: a perpetualMarkets payload containing an inactive market with `oraclePrice: null`
        // must not throw during deserialization, and the inactive market must be excluded from
        // GetFundingRatesAsync output (existing ACTIVE-only filter handles the exclusion).
        var json = """
        {
            "markets": {
                "BTC-USD": {
                    "ticker": "BTC-USD",
                    "status": "ACTIVE",
                    "oraclePrice": "50000",
                    "atomicResolution": -10,
                    "quantumConversionExponent": -9,
                    "stepSize": "0.0001",
                    "tickSize": "1",
                    "initialMarginFraction": "0.05",
                    "stepBaseQuantums": 1000000,
                    "subticksPerTick": 100000,
                    "clobPairId": "0",
                    "nextFundingRate": "0.0001"
                },
                "WTI-USD": {
                    "ticker": "WTI-USD",
                    "status": "INACTIVE",
                    "oraclePrice": null,
                    "atomicResolution": -10,
                    "quantumConversionExponent": -9,
                    "stepSize": "0.01",
                    "tickSize": "0.1",
                    "initialMarginFraction": "0.1",
                    "stepBaseQuantums": 100000000,
                    "subticksPerTick": 1000000,
                    "clobPairId": "42",
                    "nextFundingRate": "0"
                }
            }
        }
        """;

        var handler = new MultiRouteHttpMessageHandler();
        handler.AddRoute("perpetualMarkets", json);

        using var connector = BuildConnector(indexerHandler: handler);
        var rates = await connector.GetFundingRatesAsync();

        rates.Should().HaveCount(1);
        rates[0].Symbol.Should().Be("BTC");
        rates.Should().NotContain(r => r.Symbol == "WTI");
    }
}
