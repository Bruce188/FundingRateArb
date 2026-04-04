using System.Net;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Interfaces.Clients.UsdFuturesApi;
using Binance.Net.Objects.Models.Futures;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Connectors;

public class BinanceConnectorTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ResiliencePipelineProvider<string> BuildEmptyPipelineProvider()
    {
        var mock = new Mock<ResiliencePipelineProvider<string>>();
        mock.Setup(p => p.GetPipeline(It.IsAny<string>()))
            .Returns(ResiliencePipeline.Empty);
        return mock.Object;
    }

    private static ILogger<BinanceConnector> BuildNullLogger()
        => new Mock<ILogger<BinanceConnector>>().Object;

    /// <summary>
    /// Creates a successful WebCallResult wrapping an array of BinanceFuturesMarkPrice objects.
    /// </summary>
    private static WebCallResult<BinanceFuturesMarkPrice[]> SuccessMarkPrices(BinanceFuturesMarkPrice[] data)
        => new WebCallResult<BinanceFuturesMarkPrice[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a failed WebCallResult for mark prices.
    /// </summary>
    private static WebCallResult<BinanceFuturesMarkPrice[]> FailMarkPrices(string message)
        => new WebCallResult<BinanceFuturesMarkPrice[]>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    /// <summary>
    /// Creates a successful WebCallResult wrapping an array of IBinance24HPrice objects.
    /// </summary>
    private static WebCallResult<IBinance24HPrice[]> SuccessTickers(IBinance24HPrice[] data)
        => new WebCallResult<IBinance24HPrice[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a successful WebCallResult wrapping a BinanceUsdFuturesOrder.
    /// </summary>
    private static WebCallResult<BinanceUsdFuturesOrder> SuccessOrder(BinanceUsdFuturesOrder order)
        => new WebCallResult<BinanceUsdFuturesOrder>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, order, null);

    /// <summary>
    /// Creates a failed WebCallResult for an order.
    /// </summary>
    private static WebCallResult<BinanceUsdFuturesOrder> FailOrder(string message)
        => new WebCallResult<BinanceUsdFuturesOrder>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    /// <summary>
    /// Creates a successful WebCallResult for account balances.
    /// </summary>
    private static WebCallResult<BinanceUsdFuturesAccountBalance[]> SuccessBalances(BinanceUsdFuturesAccountBalance[] data)
        => new WebCallResult<BinanceUsdFuturesAccountBalance[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    private static WebCallResult<BinanceFuturesInitialLeverageChangeResult> SuccessLeverage()
        => new WebCallResult<BinanceFuturesInitialLeverageChangeResult>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, new BinanceFuturesInitialLeverageChangeResult(), null);

    /// <summary>
    /// Creates a successful WebCallResult wrapping an array of BinancePositionDetailsUsdt objects.
    /// </summary>
    private static WebCallResult<BinancePositionDetailsUsdt[]> SuccessPositions(BinancePositionDetailsUsdt[] data)
        => new WebCallResult<BinancePositionDetailsUsdt[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a failed WebCallResult for positions.
    /// </summary>
    private static WebCallResult<BinancePositionDetailsUsdt[]> FailPositions(string message)
        => new WebCallResult<BinancePositionDetailsUsdt[]>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    /// <summary>
    /// Builds a mock IBinanceRestClient wired with the given mark price response on UsdFuturesApi.ExchangeData.
    /// Also mocks GetTickersAsync with an empty array by default (needed since GetFundingRatesAsync
    /// fetches tickers in parallel for volume data).
    /// </summary>
    private static Mock<IBinanceRestClient> BuildClientWithMarkPrices(
        WebCallResult<BinanceFuturesMarkPrice[]> result)
    {
        var exchangeDataMock = new Mock<IBinanceRestClientUsdFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        exchangeDataMock
            .Setup(x => x.GetTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTickers([]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        return clientMock;
    }

    /// <summary>
    /// Builds a mock IBinanceRestClient wired with the given order result on UsdFuturesApi.Trading.
    /// Also wires Account.ChangeInitialLeverageAsync and ExchangeData.GetMarkPricesAsync (needed for
    /// PlaceMarketOrderAsync to compute quantity from mark price).
    /// </summary>
    private static Mock<IBinanceRestClient> BuildClientWithOrderResult(
        WebCallResult<BinanceUsdFuturesOrder> orderResult,
        decimal markPrice = 3500m)
    {
        var tradingMock = new Mock<IBinanceRestClientUsdFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<OrderSide>(),
                It.IsAny<FuturesOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<PositionSide?>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<OrderResponseType?>(),
                It.IsAny<bool?>(),
                It.IsAny<PriceMatch?>(),
                It.IsAny<SelfTradePreventionMode?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(orderResult);

        var accountMock = new Mock<IBinanceRestClientUsdFuturesApiAccount>();
        accountMock
            .Setup(x => x.ChangeInitialLeverageAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        // ExchangeData mock needed for GetMarkPriceAsync (called by PlaceMarketOrderAsync)
        var exchangeDataMock = new Mock<IBinanceRestClientUsdFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([
                MakeMarkPrice("ETHUSDT", markPrice, markPrice - 5m, 0.0001m),
                MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m),
            ]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        return clientMock;
    }

    private static BinanceFuturesMarkPrice MakeMarkPrice(string symbol, decimal markPrice,
        decimal indexPrice, decimal fundingRate)
        => new BinanceFuturesMarkPrice
        {
            Symbol = symbol,
            MarkPrice = markPrice,
            IndexPrice = indexPrice,
            FundingRate = fundingRate,
        };

    // ── ExchangeName ───────────────────────────────────────────────────────────

    [Fact]
    public void ExchangeName_ReturnsBinance()
    {
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        sut.ExchangeName.Should().Be("Binance");
    }

    // ── GetFundingRatesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetFundingRates_DividesBy8ForHourlyNormalization()
    {
        // Binance returns 8-hour funding rate; connector must divide by 8.
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0008m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().ContainSingle();
        rates[0].RatePerHour.Should().Be(0.0001m,
            "8-hour rate of 0.0008 divided by 8 equals 0.0001 per hour");
    }

    [Fact]
    public async Task GetFundingRates_RawRate_IsOriginalUndivided()
    {
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0008m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates[0].RawRate.Should().Be(0.0008m,
            "RawRate must store the original (undivided) 8-hour funding rate");
    }

    [Fact]
    public async Task GetFundingRates_StripsUsdtFromSymbol()
    {
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0001m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates[0].Symbol.Should().Be("ETH",
            "symbol 'ETHUSDT' must have 'USDT' suffix stripped");
    }

    [Fact]
    public async Task GetFundingRates_WhenApiFails_ThrowsInvalidOperationException()
    {
        var client = BuildClientWithMarkPrices(FailMarkPrices("API server error"));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var act = () => sut.GetFundingRatesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetFundingRates_ParsesMultipleMarketsCorrectly()
    {
        var prices = new[]
        {
            MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m),
            MakeMarkPrice("BTCUSDT", 65000m, 64980m, fundingRate: 0.0008m),
            MakeMarkPrice("SOLUSDT", 180m, 179.5m, fundingRate: -0.0004m),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(3);

        var eth = rates.Single(r => r.Symbol == "ETH");
        eth.RatePerHour.Should().Be(0.00005m);
        eth.RawRate.Should().Be(0.0004m);
        eth.MarkPrice.Should().Be(3500m);
        eth.IndexPrice.Should().Be(3495m);
        eth.ExchangeName.Should().Be("Binance");

        var btc = rates.Single(r => r.Symbol == "BTC");
        btc.RatePerHour.Should().Be(0.0001m);
        btc.RawRate.Should().Be(0.0008m);

        var sol = rates.Single(r => r.Symbol == "SOL");
        sol.RatePerHour.Should().Be(-0.00005m,
            "negative rates must also be divided by 8");
        sol.RawRate.Should().Be(-0.0004m);
    }

    // ── PlaceMarketOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_ComputesQuantity_AppliesLotSizeAndSlippage()
    {
        // Setup: mark price = 3500, sizeUsdc = 100, leverage = 10
        // Expected quantity = Math.Round(100 * 10 / 3500, 3, ToZero) = Math.Round(0.28571..., 3, ToZero) = 0.285
        decimal? capturedQuantity = null;
        decimal? capturedPrice = null;
        var tradingMock = new Mock<IBinanceRestClientUsdFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<OrderSide>(),
                It.IsAny<FuturesOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<PositionSide?>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<OrderResponseType?>(),
                It.IsAny<bool?>(),
                It.IsAny<PriceMatch?>(),
                It.IsAny<SelfTradePreventionMode?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new Moq.InvocationAction(invocation =>
                {
                    capturedQuantity = (decimal?)invocation.Arguments[3]; // qty is the 4th parameter
                    capturedPrice = (decimal?)invocation.Arguments[4]; // price is the 5th parameter
                }))
            .ReturnsAsync(SuccessOrder(new BinanceUsdFuturesOrder { Id = 1 }));

        var accountMock = new Mock<IBinanceRestClientUsdFuturesApiAccount>();
        accountMock
            .Setup(x => x.ChangeInitialLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        var exchangeDataMock = new Mock<IBinanceRestClientUsdFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        var sut = new BinanceConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 10);

        capturedQuantity.Should().NotBeNull("quantity must be computed from mark price and leverage");
        // quantity = Math.Round(100 * 10 / 3500, 3, ToZero) = Math.Round(0.28571, 3, ToZero) = 0.285
        capturedQuantity.Should().Be(0.285m, "100 USDC * 10 leverage / 3500 mark price, rounded to 3 decimals (ToZero)");

        // Limit price for Long = RoundToTickSize(3500 * 1.005, tickSize=0.01) = RoundToTickSize(3517.5, 0.01) = 3517.5
        capturedPrice.Should().NotBeNull("limit price must be set for IOC order");
        capturedPrice!.Value.Should().BeApproximately(3517.5m, 0.1m, "Long side slippage: markPrice * 1.005");
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantity_RoundsWithoutExceeding()
    {
        // Setup: input quantity = 1.23456789, precision = 3 (default when no exchange info)
        // Expected: rounded quantity = 1.234 (ToZero, never exceeds)
        decimal? capturedQuantity = null;
        var tradingMock = new Mock<IBinanceRestClientUsdFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<OrderSide>(),
                It.IsAny<FuturesOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<PositionSide?>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<OrderResponseType?>(),
                It.IsAny<bool?>(),
                It.IsAny<PriceMatch?>(),
                It.IsAny<SelfTradePreventionMode?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new Moq.InvocationAction(invocation =>
                {
                    capturedQuantity = (decimal?)invocation.Arguments[3];
                }))
            .ReturnsAsync(SuccessOrder(new BinanceUsdFuturesOrder { Id = 1, QuantityFilled = 1.234m }));

        var accountMock = new Mock<IBinanceRestClientUsdFuturesApiAccount>();
        accountMock
            .Setup(x => x.ChangeInitialLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        var exchangeDataMock = new Mock<IBinanceRestClientUsdFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        var sut = new BinanceConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, quantity: 1.23456789m, leverage: 5);

        result.Success.Should().BeTrue();
        capturedQuantity.Should().Be(1.234m, "MidpointRounding.ToZero should truncate, never exceed input quantity");
    }

    [Fact]
    public async Task GetFundingRates_Normalizes8hToHourly()
    {
        // rawRate = 0.0008 → ratePerHour = 0.0001
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0008m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().ContainSingle();
        rates[0].RatePerHour.Should().Be(0.0001m, "0.0008 / 8 = 0.0001");
        rates[0].Symbol.Should().Be("ETH", "USDT suffix stripped");
    }

    // ── HasOpenPositionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task HasOpenPosition_DetectsByPositionSide()
    {
        var position = new BinancePositionDetailsUsdt
        {
            Symbol = "ETHUSDT",
            Quantity = 0.5m, // positive = Long
        };

        var accountMock = new Mock<IBinanceRestClientUsdFuturesApiAccount>();
        accountMock
            .Setup(x => x.GetPositionInformationAsync(
                It.IsAny<string>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessPositions([position]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        var sut = new BinanceConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var longResult = await sut.HasOpenPositionAsync("ETH", Side.Long);
        var shortResult = await sut.HasOpenPositionAsync("ETH", Side.Short);

        longResult.Should().BeTrue("position quantity > 0 means Long");
        shortResult.Should().BeFalse("position quantity > 0 means not Short");
    }

    // ── ClosePositionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_FetchesPositionAndPlacesReduceOnly()
    {
        // Setup: position with quantity = 0.5 for ETHUSDT
        var position = new BinancePositionDetailsUsdt
        {
            Symbol = "ETHUSDT",
            Quantity = 0.5m,
        };

        OrderSide? capturedSide = null;
        bool? capturedReduceOnly = null;
        decimal? capturedQuantity = null;

        var tradingMock = new Mock<IBinanceRestClientUsdFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<OrderSide>(),
                It.IsAny<FuturesOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<PositionSide?>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<OrderResponseType?>(),
                It.IsAny<bool?>(),
                It.IsAny<PriceMatch?>(),
                It.IsAny<SelfTradePreventionMode?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new Moq.InvocationAction(invocation =>
            {
                capturedSide = (OrderSide)invocation.Arguments[1];
                capturedQuantity = (decimal?)invocation.Arguments[3];
                capturedReduceOnly = (bool?)invocation.Arguments[7];
            }))
            .ReturnsAsync(SuccessOrder(new BinanceUsdFuturesOrder { Id = 3 }));

        var accountMock = new Mock<IBinanceRestClientUsdFuturesApiAccount>();
        accountMock
            .Setup(x => x.GetPositionInformationAsync(
                It.IsAny<string>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessPositions([position]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        var sut = new BinanceConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        capturedSide.Should().Be(OrderSide.Sell, "closing a Long requires a Sell");
        capturedReduceOnly.Should().BeTrue("close orders must be reduce-only");
        capturedQuantity.Should().Be(0.5m, "must close the full position quantity");

        // Verify GetPositionInformationAsync was called
        accountMock.Verify(
            x => x.GetPositionInformationAsync("ETHUSDT", It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaceMarketOrder_ReturnsSuccessResult_WhenSdkSucceeds()
    {
        var binanceOrder = new BinanceUsdFuturesOrder
        {
            Id = 98765,
            AveragePrice = 3510.50m,
            QuantityFilled = 0.05m,
        };
        var client = BuildClientWithOrderResult(SuccessOrder(binanceOrder));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        result.Success.Should().BeTrue();
        result.OrderId.Should().Be("98765");
        result.FilledPrice.Should().Be(3510.50m);
        result.FilledQuantity.Should().Be(0.05m);
    }

    [Fact]
    public async Task PlaceMarketOrder_ReturnsFailureResult_WhenSdkFails()
    {
        var client = BuildClientWithOrderResult(FailOrder("Insufficient balance"));
        var sut = new BinanceConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty("error message must be populated on failure");
    }

    [Fact]
    public async Task PlaceMarketOrder_MapsLongSideToSdkBuy()
    {
        var tradingMock = new Mock<IBinanceRestClientUsdFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                OrderSide.Buy,
                It.IsAny<FuturesOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<PositionSide?>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<OrderResponseType?>(),
                It.IsAny<bool?>(),
                It.IsAny<PriceMatch?>(),
                It.IsAny<SelfTradePreventionMode?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new BinanceUsdFuturesOrder { Id = 1 }));

        var accountMock = new Mock<IBinanceRestClientUsdFuturesApiAccount>();
        accountMock
            .Setup(x => x.ChangeInitialLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        var exchangeDataMock = new Mock<IBinanceRestClientUsdFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IBinanceRestClientUsdFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IBinanceRestClient>();
        clientMock.SetupGet(c => c.UsdFuturesApi).Returns(futuresApiMock.Object);

        var sut = new BinanceConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5);

        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                OrderSide.Buy,
                It.IsAny<FuturesOrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<PositionSide?>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<OrderResponseType?>(),
                It.IsAny<bool?>(),
                It.IsAny<PriceMatch?>(),
                It.IsAny<SelfTradePreventionMode?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
