using System.Net;
using Aster.Net.Interfaces.Clients;
using Aster.Net.Interfaces.Clients.FuturesApi;
using Aster.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using FluentAssertions;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Connectors;

public class AsterConnectorTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ResiliencePipelineProvider<string> BuildEmptyPipelineProvider()
    {
        var mock = new Mock<ResiliencePipelineProvider<string>>();
        mock.Setup(p => p.GetPipeline(It.IsAny<string>()))
            .Returns(ResiliencePipeline.Empty);
        return mock.Object;
    }

    /// <summary>
    /// Creates a successful WebCallResult wrapping an array of AsterMarkPrice objects.
    /// </summary>
    private static WebCallResult<AsterMarkPrice[]> SuccessMarkPrices(AsterMarkPrice[] data)
        => new WebCallResult<AsterMarkPrice[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a failed WebCallResult for mark prices.
    /// </summary>
    private static WebCallResult<AsterMarkPrice[]> FailMarkPrices(string message)
        => new WebCallResult<AsterMarkPrice[]>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    /// <summary>
    /// Creates a successful WebCallResult wrapping an array of AsterTicker objects.
    /// </summary>
    private static WebCallResult<AsterTicker[]> SuccessTickers(AsterTicker[] data)
        => new WebCallResult<AsterTicker[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a successful WebCallResult wrapping an AsterOrder.
    /// </summary>
    private static WebCallResult<AsterOrder> SuccessOrder(AsterOrder order)
        => new WebCallResult<AsterOrder>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, order, null);

    /// <summary>
    /// Creates a failed WebCallResult for an order.
    /// </summary>
    private static WebCallResult<AsterOrder> FailOrder(string message)
        => new WebCallResult<AsterOrder>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    /// <summary>
    /// Creates a successful WebCallResult for account balances.
    /// </summary>
    private static WebCallResult<AsterBalance[]> SuccessBalances(AsterBalance[] data)
        => new WebCallResult<AsterBalance[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a failed WebCallResult for account balances.
    /// </summary>
    private static WebCallResult<AsterBalance[]> FailBalances(string message)
        => new WebCallResult<AsterBalance[]>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    private static WebCallResult<AsterLeverage> SuccessLeverage()
        => new WebCallResult<AsterLeverage>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, new AsterLeverage(), null);

    /// <summary>
    /// Builds a mock IAsterRestClient wired with the given mark price response on FuturesApi.ExchangeData.
    /// Also mocks GetTickersAsync with an empty array by default (needed since GetFundingRatesAsync
    /// fetches tickers in parallel for volume data).
    /// </summary>
    private static Mock<IAsterRestClient> BuildClientWithMarkPrices(
        WebCallResult<AsterMarkPrice[]> result)
    {
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        exchangeDataMock
            .Setup(x => x.GetTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTickers([]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        return clientMock;
    }

    /// <summary>
    /// Builds a mock IAsterRestClient wired with the given order result on FuturesApi.Trading.
    /// Also wires Account.SetLeverageAsync and ExchangeData.GetMarkPricesAsync (needed for
    /// PlaceMarketOrderAsync to compute quantity from mark price).
    /// </summary>
    private static Mock<IAsterRestClient> BuildClientWithOrderResult(
        WebCallResult<AsterOrder> orderResult,
        decimal markPrice = 3500m)
    {
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<Aster.Net.Enums.OrderSide>(),
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(orderResult);

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.SetLeverageAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        // ExchangeData mock needed for GetMarkPriceAsync (called by PlaceMarketOrderAsync)
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([
                MakeMarkPrice("ETHUSDT", markPrice, markPrice - 5m, 0.0001m),
                MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m),
            ]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        return clientMock;
    }

    private static AsterMarkPrice MakeMarkPrice(string symbol, decimal markPrice,
        decimal indexPrice, decimal fundingRate)
        => new AsterMarkPrice
        {
            Symbol = symbol,
            MarkPrice = markPrice,
            IndexPrice = indexPrice,
            FundingRate = fundingRate,
        };

    // ── ExchangeName ───────────────────────────────────────────────────────────

    [Fact]
    public void ExchangeName_ReturnsAster()
    {
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        sut.ExchangeName.Should().Be("Aster");
    }

    // ── GetFundingRatesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetFundingRates_DividesBy4ForHourlyNormalization()
    {
        // Aster returns 4-hour funding rate; connector must divide by 4.
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().ContainSingle();
        rates[0].RatePerHour.Should().Be(0.0001m,
            "4-hour rate of 0.0004 divided by 4 equals 0.0001 per hour");
    }

    [Fact]
    public async Task GetFundingRates_RawRate_IsOriginalUndivided()
    {
        // RawRate must preserve the original 4-hour rate, not the normalised per-hour rate.
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates[0].RawRate.Should().Be(0.0004m,
            "RawRate must store the original (undivided) 4-hour funding rate");
    }

    [Fact]
    public async Task GetFundingRates_StripsUsdtFromSymbol()
    {
        // "ETHUSDT" → "ETH"
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0001m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates[0].Symbol.Should().Be("ETH",
            "symbol 'ETHUSDT' must have 'USDT' suffix stripped");
    }

    [Fact]
    public async Task GetFundingRates_WhenApiFails_ThrowsInvalidOperationException()
    {
        var client = BuildClientWithMarkPrices(FailMarkPrices("API server error"));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var act = () => sut.GetFundingRatesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*API server error*");
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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(3);

        var eth = rates.Single(r => r.Symbol == "ETH");
        eth.RatePerHour.Should().Be(0.0001m);
        eth.RawRate.Should().Be(0.0004m);
        eth.MarkPrice.Should().Be(3500m);
        eth.IndexPrice.Should().Be(3495m);
        eth.ExchangeName.Should().Be("Aster");

        var btc = rates.Single(r => r.Symbol == "BTC");
        btc.RatePerHour.Should().Be(0.0002m);
        btc.RawRate.Should().Be(0.0008m);

        var sol = rates.Single(r => r.Symbol == "SOL");
        sol.RatePerHour.Should().Be(-0.0001m,
            "negative rates must also be divided by 4");
        sol.RawRate.Should().Be(-0.0004m);
    }

    [Fact]
    public async Task GetFundingRates_SetsExchangeNameOnAllDtos()
    {
        var prices = new[]
        {
            MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0004m),
            MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0008m),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().AllSatisfy(r => r.ExchangeName.Should().Be("Aster"));
    }

    [Fact]
    public async Task GetFundingRates_WhenEmptyResponse_ReturnsEmptyList()
    {
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFundingRates_SymbolNormalization_OnlyStripsSuffix()
    {
        // Symbol "USDTUSDT" should become "USDT" (not empty string from Replace("USDT",""))
        // Symbol "ETHUSDT" should become "ETH"
        // Symbol "USTCUSDT" should become "USTC" (not "C" from greedy Replace)
        var prices = new[]
        {
            MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m),
            MakeMarkPrice("USTCUSDT", 0.02m, 0.02m, 0.0001m),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(2);
        rates.Select(r => r.Symbol).Should().BeEquivalentTo(new[] { "ETH", "USTC" });
    }

    // ── PlaceMarketOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_ComputesQuantityFromMarkPrice()
    {
        // Verify that quantity = sizeUsdc / markPrice is passed (not null)
        decimal? capturedQuantity = null;
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<Aster.Net.Enums.OrderSide>(),
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new Moq.InvocationAction(invocation =>
                {
                    capturedQuantity = (decimal?)invocation.Arguments[3]; // qty is the 4th parameter
                }))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 1 }));

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        // Mark price = 2500 so quantity = 100 / 2500 = 0.04
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 2500m, 2495m, 0.0001m)]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 1);

        capturedQuantity.Should().NotBeNull("quantity must be computed from mark price, not null");
        capturedQuantity.Should().Be(0.04m, "100 USDC / 2500 mark price = 0.04");
    }

    [Fact]
    public async Task PlaceMarketOrder_ReturnsSuccessResult_WhenSdkSucceeds()
    {
        var asterOrder = new AsterOrder
        {
            Id = 98765,
            AveragePrice = 3510.50m,
            QuantityFilled = 0.05m,
        };
        var client = BuildClientWithOrderResult(SuccessOrder(asterOrder));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Insufficient balance");
    }

    [Fact]
    public async Task PlaceMarketOrder_MapsLongSideToSdkBuy()
    {
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Buy,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 1 }));

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5);

        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Buy,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaceMarketOrder_MapsShortSideToSdkSell()
    {
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Sell,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 2 }));

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        await sut.PlaceMarketOrderAsync("ETH", Side.Short, 100m, 5);

        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Sell,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── ClosePositionAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_Long_PlacesSellOrderWithReduceOnly()
    {
        // Closing a Long position → place Sell order with reduceOnly = true
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Sell,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                true,                               // reduceOnly must be true
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 3 }));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Sell,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                true,
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClosePosition_Short_PlacesBuyOrderWithReduceOnly()
    {
        // Closing a Short position → place Buy order with reduceOnly = true
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Buy,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                true,
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 4 }));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        await sut.ClosePositionAsync("ETH", Side.Short);

        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Buy,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                true,
                It.IsAny<string>(),
                It.IsAny<decimal?>(),
                It.IsAny<bool?>(),
                It.IsAny<decimal?>(),
                It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── GetMarkPriceAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMarkPrice_ReturnsCorrectPrice()
    {
        var prices = new[]
        {
            MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m),
            MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var price = await sut.GetMarkPriceAsync("ETH");

        price.Should().Be(3500m);
    }

    [Fact]
    public async Task GetMarkPrice_WhenAssetNotFound_ThrowsKeyNotFoundException()
    {
        var prices = new[] { MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m) };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider());

        var act = () => sut.GetMarkPriceAsync("DOGE");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ── GetAvailableBalanceAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetAvailableBalance_ReturnsCorrectBalance()
    {
        var balance = new AsterBalance { AvailableBalance = 250.75m };
        var balanceResult = SuccessBalances([balance]);

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(balanceResult);

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        var available = await sut.GetAvailableBalanceAsync();

        available.Should().Be(250.75m);
    }

    [Fact]
    public async Task GetAvailableBalance_WhenApiFails_ThrowsInvalidOperationException()
    {
        var failResult = FailBalances("Balance fetch failed");

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        var act = () => sut.GetAvailableBalanceAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Balance fetch failed*");
    }

    [Fact]
    public async Task GetAvailableBalance_SumsAvailableBalances_WhenMultipleAssets()
    {
        var balances = new[]
        {
            new AsterBalance { AvailableBalance = 100m },
            new AsterBalance { AvailableBalance = 50m },
        };
        var balanceResult = SuccessBalances(balances);

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(balanceResult);

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider());

        var available = await sut.GetAvailableBalanceAsync();

        available.Should().Be(150m);
    }
}
