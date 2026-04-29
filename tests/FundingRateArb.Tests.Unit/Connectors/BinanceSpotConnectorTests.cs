using Binance.Net.Interfaces.Clients;
using Binance.Net.Interfaces.Clients.SpotApi;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Connectors;

/// <summary>
/// Tests for BinanceConnector — Spot-specific behaviour:
/// MarketType is Spot, GetFundingRatesAsync returns empty,
/// leverage warning logged but no throw, short-side rejection.
/// </summary>
public class BinanceSpotConnectorTests
{
    private readonly Mock<IBinanceRestClient> _restClient = new();
    private readonly Mock<IBinanceRestClientSpotApi> _spotApi = new();
    private readonly Mock<IBinanceRestClientSpotApiTrading> _spotTrading = new();
    private readonly Mock<IBinanceRestClientSpotApiAccount> _spotAccount = new();
    private readonly Mock<IBinanceRestClientSpotApiExchangeData> _spotExchangeData = new();
    private readonly Mock<ResiliencePipelineProvider<string>> _pipelineProvider = new();
    private readonly Mock<IMarkPriceCache> _markPriceCache = new();

    private BinanceConnector CreateConnector(ILogger<BinanceConnector>? logger = null)
    {
        _restClient.Setup(c => c.SpotApi).Returns(_spotApi.Object);
        _spotApi.Setup(s => s.Trading).Returns(_spotTrading.Object);
        _spotApi.Setup(s => s.Account).Returns(_spotAccount.Object);
        _spotApi.Setup(s => s.ExchangeData).Returns(_spotExchangeData.Object);
        _pipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>()))
            .Returns(ResiliencePipeline.Empty);

        return new BinanceConnector(
            _restClient.Object,
            _pipelineProvider.Object,
            logger ?? NullLogger<BinanceConnector>.Instance,
            _markPriceCache.Object);
    }

    // ── MarketType ───────────────────────────────────────────────────────────

    [Fact]
    public void MarketType_IsSpot()
    {
        var sut = CreateConnector();
        sut.MarketType.Should().Be(ExchangeMarketType.Spot);
    }

    // ── GetFundingRatesAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetFundingRatesAsync_ReturnsEmptyList()
    {
        var sut = CreateConnector();
        var rates = await sut.GetFundingRatesAsync(CancellationToken.None);
        rates.Should().BeEmpty("Spot has no funding settlement");
    }

    // ── Short-side rejection (by-quantity) ───────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_ShortSide_ReturnsFailure()
    {
        var sut = CreateConnector();
        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, 0.1m, leverage: 1, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Spot does not support shorting");
    }

    // ── Short-side rejection (by-notional) ──────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrderAsync_ShortSide_ReturnsFailure()
    {
        var sut = CreateConnector();
        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Short, 100m, leverage: 1, ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Spot does not support shorting");
    }

    // ── GetMaxLeverageAsync always returns 1 ─────────────────────────────────

    [Fact]
    public async Task GetMaxLeverageAsync_ReturnsOne()
    {
        var sut = CreateConnector();
        var leverage = await sut.GetMaxLeverageAsync("ETH");
        leverage.Should().Be(1, "Spot has no leverage");
    }

    // ── Leverage warning on PlaceMarketOrderByQuantityAsync ─────────────────

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_Leverage5_LogsWarningAndDoesNotThrow()
    {
        // The leverage check fires BEFORE any API call — just verify the warning is logged.
        // BinanceConnector calls GetExchangeInfoAsync(ct: token) which resolves to the
        // (bool?, SymbolStatus?, CancellationToken) overload.
        var loggerMock = new Mock<ILogger<BinanceConnector>>();

        _spotExchangeData
            .Setup(e => e.GetExchangeInfoAsync(
                It.IsAny<bool?>(),
                It.IsAny<Binance.Net.Enums.SymbolStatus?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFailedWebCallResult<Binance.Net.Objects.Models.Spot.BinanceExchangeInfo>());

        // PlaceOrderAsync must return a non-null result (even a failed one) to prevent NullRef.
        // The method signature: (string, OrderSide, SpotOrderType, decimal?, decimal?, string?,
        //   decimal?, TimeInForce?, decimal?, decimal?, OrderResponseType?, int?, int?, int?,
        //   SelfTradePreventionMode?, PegPriceType?, int?, PegOffsetType?, int?, CancellationToken)
        _spotTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<Binance.Net.Enums.OrderSide>(),
                It.IsAny<Binance.Net.Enums.SpotOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Binance.Net.Enums.TimeInForce?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Binance.Net.Enums.OrderResponseType?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<Binance.Net.Enums.SelfTradePreventionMode?>(),
                It.IsAny<Binance.Net.Enums.PegPriceType?>(),
                It.IsAny<int?>(),
                It.IsAny<Binance.Net.Enums.PegOffsetType?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFailedWebCallResult<Binance.Net.Objects.Models.Spot.BinancePlacedOrder>());

        var sut = CreateConnector(loggerMock.Object);

        // Must not throw even with leverage = 5 (spot logs warning and proceeds; REST may fail)
        var act = async () => await sut.PlaceMarketOrderByQuantityAsync(
            "ETH", Side.Long, 0.1m, leverage: 5, ct: CancellationToken.None);
        await act.Should().NotThrowAsync();

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("leverage")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "Leverage != 1 must trigger a warning log");
    }

    // ── GetAllOpenPositionsAsync — API failure returns null gracefully ────────

    [Fact]
    public async Task GetAllOpenPositionsAsync_ApiFailure_ReturnsNull()
    {
        // GetAllOpenPositionsAsync calls GetAccountInfoAsync; on failure it must return null
        _spotAccount
            .Setup(a => a.GetAccountInfoAsync(It.IsAny<bool?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFailedWebCallResult<Binance.Net.Objects.Models.Spot.BinanceAccountInfo>());

        var sut = CreateConnector();
        var positions = await sut.GetAllOpenPositionsAsync();

        positions.Should().BeNull("API failure must return null rather than throw");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WebCallResult<T> BuildFailedWebCallResult<T>()
    {
        var errorInfo = new ErrorInfo(ErrorType.Unknown, "simulated error");
        return new WebCallResult<T>(new ServerError(errorInfo, null));
    }
}
