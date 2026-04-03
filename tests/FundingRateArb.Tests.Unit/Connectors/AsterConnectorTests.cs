using System.Net;
using Aster.Net.Interfaces.Clients;
using Aster.Net.Interfaces.Clients.FuturesApi;
using Aster.Net.Objects.Models;
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

    private static ILogger<AsterConnector> BuildNullLogger()
        => new Mock<ILogger<AsterConnector>>().Object;

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
    /// Creates a successful WebCallResult wrapping an array of AsterPosition objects.
    /// </summary>
    private static WebCallResult<AsterPosition[]> SuccessPositions(AsterPosition[] data)
        => new WebCallResult<AsterPosition[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    /// <summary>
    /// Creates a failed WebCallResult for positions.
    /// </summary>
    private static WebCallResult<AsterPosition[]> FailPositions(string message)
        => new WebCallResult<AsterPosition[]>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        sut.ExchangeName.Should().Be("Aster");
    }

    // ── GetFundingRatesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetFundingRates_DividesBy8ForHourlyNormalization()
    {
        // Aster returns 8-hour funding rate; connector must divide by 8.
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().ContainSingle();
        rates[0].RatePerHour.Should().Be(0.00005m,
            "8-hour rate of 0.0004 divided by 8 equals 0.00005 per hour");
    }

    [Fact]
    public async Task GetFundingRates_RawRate_IsOriginalUndivided()
    {
        // RawRate must preserve the original 8-hour rate, not the normalised per-hour rate.
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates[0].RawRate.Should().Be(0.0004m,
            "RawRate must store the original (undivided) 8-hour funding rate");
    }

    [Fact]
    public async Task GetFundingRates_StripsUsdtFromSymbol()
    {
        // "ETHUSDT" → "ETH"
        var markPrice = MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0001m);
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([markPrice]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates[0].Symbol.Should().Be("ETH",
            "symbol 'ETHUSDT' must have 'USDT' suffix stripped");
    }

    [Fact]
    public async Task GetFundingRates_WhenApiFails_ThrowsInvalidOperationException()
    {
        var client = BuildClientWithMarkPrices(FailMarkPrices("API server error"));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(3);

        var eth = rates.Single(r => r.Symbol == "ETH");
        eth.RatePerHour.Should().Be(0.00005m);
        eth.RawRate.Should().Be(0.0004m);
        eth.MarkPrice.Should().Be(3500m);
        eth.IndexPrice.Should().Be(3495m);
        eth.ExchangeName.Should().Be("Aster");

        var btc = rates.Single(r => r.Symbol == "BTC");
        btc.RatePerHour.Should().Be(0.0001m);
        btc.RawRate.Should().Be(0.0008m);

        var sol = rates.Single(r => r.Symbol == "SOL");
        sol.RatePerHour.Should().Be(-0.00005m,
            "negative rates must also be divided by 8");
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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().AllSatisfy(r => r.ExchangeName.Should().Be("Aster"));
    }

    [Fact]
    public async Task GetFundingRates_WhenEmptyResponse_ReturnsEmptyList()
    {
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(2);
        rates.Select(r => r.Symbol).Should().BeEquivalentTo(new[] { "ETH", "USTC" });
    }

    // ── PlaceMarketOrderAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_ComputesQuantityFromMarkPriceAndLeverage()
    {
        // Verify that quantity = sizeUsdc * leverage / markPrice (leverage must be applied)
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

        // Mark price = 2500, leverage = 5
        // Expected quantity = 100 * 5 / 2500 = 0.2 (NOT 100/2500 = 0.04 which would be wrong)
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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 5);

        capturedQuantity.Should().NotBeNull("quantity must be computed from mark price and leverage");
        capturedQuantity.Should().Be(0.2m, "100 USDC * 5 leverage / 2500 mark price = 0.2");
    }

    [Fact]
    public async Task PlaceMarketOrder_LeverageOneGivesCorrectQuantity()
    {
        // With leverage = 1: quantity = sizeUsdc * 1 / markPrice = sizeUsdc / markPrice
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
                    capturedQuantity = (decimal?)invocation.Arguments[3];
                }))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 1 }));

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());

        // Mark price = 2500, leverage = 1 → quantity = 100 * 1 / 2500 = 0.04
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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 1);

        capturedQuantity.Should().Be(0.04m, "100 USDC * 1 leverage / 2500 mark price = 0.04");
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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty("error message must be populated on failure");
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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

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

    /// <summary>
    /// Builds a mock IAsterRestClient wired for ClosePositionAsync tests.
    /// Sets up GetPositionsAsync to return the given position data, and PlaceOrderAsync for the close order.
    /// </summary>
    private static (Mock<IAsterRestClient> clientMock, Mock<IAsterRestClientFuturesApiTrading> tradingMock) BuildClientForClose(
        WebCallResult<AsterPosition[]> positionsResult,
        WebCallResult<AsterOrder>? orderResult = null)
    {
        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.GetPositionsAsync(
                It.IsAny<string>(),
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(positionsResult);

        if (orderResult != null)
        {
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
        }

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        return (clientMock, tradingMock);
    }

    [Fact]
    public async Task ClosePosition_Long_PlacesSellOrderWithReduceOnly()
    {
        // Closing a Long position → fetch position, place Sell order with reduceOnly = true
        var position = new AsterPosition { Symbol = "ETHUSDT", PositionAmount = 0.5m };
        var (clientMock, tradingMock) = BuildClientForClose(
            SuccessPositions([position]),
            SuccessOrder(new AsterOrder { Id = 3 }));

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Sell,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                0.5m,                               // explicit quantity from position
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
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ClosePosition_Short_PlacesBuyOrderWithReduceOnly()
    {
        // Closing a Short position → place Buy order with reduceOnly = true and abs(quantity)
        var position = new AsterPosition { Symbol = "ETHUSDT", PositionAmount = -0.3m };
        var (clientMock, tradingMock) = BuildClientForClose(
            SuccessPositions([position]),
            SuccessOrder(new AsterOrder { Id = 4 }));

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.ClosePositionAsync("ETH", Side.Short);

        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(),
                Aster.Net.Enums.OrderSide.Buy,
                It.IsAny<Aster.Net.Enums.OrderType>(),
                0.3m,                               // abs(-0.3) = 0.3
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
    public async Task ClosePosition_CallsGetPositionsAndPassesNonNullQuantity()
    {
        // C1 test: ClosePosition calls GetPositionsAsync and passes non-null quantity to PlaceOrderAsync
        decimal? capturedQuantity = null;
        var position = new AsterPosition { Symbol = "ETHUSDT", PositionAmount = 1.25m };

        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        tradingMock
            .Setup(x => x.GetPositionsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessPositions([position]));
        tradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(),
                It.IsAny<Aster.Net.Enums.OrderType>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(), It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<bool?>(),
                It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback(new Moq.InvocationAction(invocation =>
            {
                capturedQuantity = (decimal?)invocation.Arguments[3];
            }))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 1 }));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(tradingMock.Object);
        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());
        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        tradingMock.Verify(x => x.GetPositionsAsync("ETHUSDT", It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once);
        capturedQuantity.Should().NotBeNull("quantity must be fetched from position, not null");
        capturedQuantity.Should().Be(1.25m);
    }

    [Fact]
    public async Task ClosePosition_ReturnsFailure_WhenNoPositionFound()
    {
        // C1 test: ClosePosition returns failure when no position found on Aster
        var (clientMock, _) = BuildClientForClose(SuccessPositions([]));

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());
        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("No open position");
    }

    [Fact]
    public async Task ClosePosition_ReturnsFailure_WhenGetPositionsFails()
    {
        // C1 test: ClosePosition returns failure when GetPositionsAsync fails
        var (clientMock, _) = BuildClientForClose(FailPositions("API error"));

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());
        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Failed to fetch position");
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
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var price = await sut.GetMarkPriceAsync("ETH");

        price.Should().Be(3500m);
    }

    [Fact]
    public async Task GetMarkPrice_WhenAssetNotFound_ThrowsKeyNotFoundException()
    {
        var prices = new[] { MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m) };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var act = () => sut.GetMarkPriceAsync("DOGE");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GetMarkPrice_CachePreventsRedundantApiCalls()
    {
        // Two consecutive GetMarkPriceAsync calls for the same asset must only hit the API once.
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var price1 = await sut.GetMarkPriceAsync("ETH");
        var price2 = await sut.GetMarkPriceAsync("ETH");

        price1.Should().Be(3500m);
        price2.Should().Be(3500m);
        exchangeDataMock.Verify(
            x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "The mark price endpoint should only be fetched once; second call must use cache");
    }

    [Fact]
    public async Task GetMarkPrice_CacheIsSharedAcrossMultipleAssets()
    {
        // Fetching ETH then BTC within TTL should only call the API once.
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([
                MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m),
                MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m),
            ]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var ethPrice = await sut.GetMarkPriceAsync("ETH");
        var btcPrice = await sut.GetMarkPriceAsync("BTC");

        ethPrice.Should().Be(3500m);
        btcPrice.Should().Be(65000m);
        exchangeDataMock.Verify(
            x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "Fetching a second asset within TTL must use cached mark price data");
    }

    // ── IDisposable (C4) ──────────────────────────────────────────────────────

    [Fact]
    public void AsterConnector_ImplementsIDisposable()
    {
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        sut.Should().BeAssignableTo<IDisposable>(
            "AsterConnector must implement IDisposable to release the SemaphoreSlim");
    }

    [Fact]
    public void AsterConnector_Dispose_DoesNotThrow()
    {
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var act = () => ((IDisposable)sut).Dispose();
        act.Should().NotThrow("Dispose must be safe to call");
    }

    // ── GetMarkPrice cache post-lock read (H6) ─────────────────────────────────

    [Fact]
    public async Task GetMarkPrice_CacheHit_DoesNotPerformExtraHttpCall()
    {
        // Three calls within TTL should result in exactly one API call
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([
                MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m),
                MakeMarkPrice("BTCUSDT", 65000m, 64980m, 0.0001m),
            ]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var price1 = await sut.GetMarkPriceAsync("ETH");
        var price2 = await sut.GetMarkPriceAsync("ETH");
        var price3 = await sut.GetMarkPriceAsync("BTC");

        price1.Should().Be(3500m);
        price2.Should().Be(3500m);
        price3.Should().Be(65000m);

        exchangeDataMock.Verify(
            x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "All subsequent calls within TTL must use cached result without re-fetching");
    }

    // ── GetAvailableBalanceAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetAvailableBalance_ReturnsCorrectBalance()
    {
        var balance = new AsterBalance { Asset = "USDT", AvailableBalance = 250.75m };
        var balanceResult = SuccessBalances([balance]);

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(balanceResult);

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var act = () => sut.GetAvailableBalanceAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetAvailableBalance_ReturnsOnlyUsdtBalance_NotSumOfAllAssets()
    {
        // H7 test: only USDT balance counts, not BTC or other assets
        var balances = new[]
        {
            new AsterBalance { Asset = "USDT", AvailableBalance = 100m },
            new AsterBalance { Asset = "BTC", AvailableBalance = 50m },
            new AsterBalance { Asset = "ETH", AvailableBalance = 25m },
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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var available = await sut.GetAvailableBalanceAsync();

        available.Should().Be(100m,
            "only USDT balance should be counted, not the sum of all assets");
    }

    // ── B-W3: SetLeverage failure is logged, order still proceeds ─────────────

    [Fact]
    public async Task PlaceMarketOrder_WhenSetLeverageFails_AbortsOrder()
    {
        // Arrange — SetLeverageAsync returns a failure result
        var loggerMock = new Mock<ILogger<AsterConnector>>();

        var failLeverage = new WebCallResult<AsterLeverage>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, "leverage limit exceeded"), null!));

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failLeverage);

        var tradingMock = new Mock<IAsterRestClientFuturesApiTrading>();

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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), loggerMock.Object, new SingletonMarkPriceCache());

        // Act
        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 5);

        // Assert — order must be aborted when SetLeverage fails
        result.Success.Should().BeFalse("leverage failure now aborts the order");
        result.Error.Should().Contain("leverage");

        // No order should have been placed
        tradingMock.Verify(
            x => x.PlaceOrderAsync(
                It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(),
                It.IsAny<Aster.Net.Enums.OrderType>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.PositionSide?>(), It.IsAny<Aster.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(), It.IsAny<string>(), It.IsAny<decimal?>(), It.IsAny<bool?>(),
                It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.WorkingType?>(),
                It.IsAny<bool?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no order should be placed when SetLeverage fails");
    }

    // ── D3: Connector safety tests ────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_MarkPriceZero_ReturnsFalse()
    {
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 0m, 0m, 0.0001m)]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock.Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<long?>(), It.IsAny<CancellationToken>())).ReturnsAsync(SuccessLeverage());
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("zero");
    }

    [Fact]
    public async Task PlaceMarketOrder_ZeroQuantity_ReturnsFalse()
    {
        // sizeUsdc=0 → quantity = 0*5/3500 = 0
        var client = BuildClientWithOrderResult(SuccessOrder(new AsterOrder { Id = 1 }), markPrice: 3500m);
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 0m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("zero");
    }

    [Fact]
    public async Task PlaceMarketOrder_LeverageSetFails_ReturnsFalse()
    {
        // Already tested above as PlaceMarketOrder_WhenSetLeverageFails_AbortsOrder
        // but with different framing for D3 coverage
        var failLeverage = new WebCallResult<AsterLeverage>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, "not supported"), null!));

        var accountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        accountMock.Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<long?>(), It.IsAny<CancellationToken>())).ReturnsAsync(failLeverage);

        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock.Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", 3500m, 3495m, 0.0001m)]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Account).Returns(accountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);
        futuresApiMock.SetupGet(f => f.Trading).Returns(new Mock<IAsterRestClientFuturesApiTrading>().Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task PlaceMarketOrder_WhenSetLeverageSucceeds_NoWarningLogged()
    {
        // Arrange — SetLeverageAsync succeeds
        var loggerMock = new Mock<ILogger<AsterConnector>>();

        var asterOrder = new AsterOrder { Id = 2, AveragePrice = 3500m, QuantityFilled = 0.1m };
        var client = BuildClientWithOrderResult(SuccessOrder(asterOrder));

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), loggerMock.Object, new SingletonMarkPriceCache());

        // Act
        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 5);

        // Assert — no warning logged for leverage
        result.Success.Should().BeTrue();
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("leverage")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "no warning should be logged when SetLeverage succeeds");
    }

    // ── NB7: Min notional validation ─────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_BelowMinNotional_ReturnsFalse()
    {
        // sizeUsdc=3.5, leverage=1, mark=3500 → quantity=3.5/3500=0.001 (at 3 decimal default), notional=0.001*3500=$3.50 < $5
        var client = BuildClientWithOrderResult(SuccessOrder(new AsterOrder { Id = 1 }), markPrice: 3500m);
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 3.5m, 1);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("below Aster minimum");
    }

    // ── B3: RoundToTickSize via reflection ───────────────────────────────────────

    [Fact]
    public void RoundToTickSize_TickSize0_5_RoundsToNearestTick()
    {
        var method = typeof(AsterConnector).GetMethod("RoundToTickSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("RoundToTickSize must exist as a private static method");

        // 3517.3 / 0.5 = 7034.6 → rounds to 7035 → 7035 * 0.5 = 3517.5
        var result = (decimal)method!.Invoke(null, new object[] { 3517.3m, 0.5m })!;

        result.Should().Be(3517.5m,
            "3517.3 rounded to nearest 0.5 tick should be 3517.5");
    }

    [Fact]
    public void RoundToTickSize_TickSize0_0001_RoundsToNearestTick()
    {
        var method = typeof(AsterConnector).GetMethod("RoundToTickSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("RoundToTickSize must exist as a private static method");

        // 0.02887 / 0.0001 = 288.7 → rounds to 289 → 289 * 0.0001 = 0.0289
        var result = (decimal)method!.Invoke(null, new object[] { 0.02887m, 0.0001m })!;

        result.Should().Be(0.0289m,
            "0.02887 rounded to nearest 0.0001 tick should be 0.0289");
    }

    [Fact]
    public void RoundToTickSize_ZeroTickSize_FallsBackTo2dp()
    {
        var method = typeof(AsterConnector).GetMethod("RoundToTickSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("RoundToTickSize must exist as a private static method");

        var result = (decimal)method!.Invoke(null, new object[] { 3517.256m, 0m })!;

        result.Should().Be(3517.26m,
            "zero tick size must fall back to 2dp rounding");
    }

    // ── NB8: Slippage protection uses Limit+IOC ──────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_UsesLimitIocWithSlippage()
    {
        Aster.Net.Enums.OrderType? capturedOrderType = null;
        Aster.Net.Enums.TimeInForce? capturedTif = null;
        decimal? capturedPrice = null;

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
                    capturedOrderType = (Aster.Net.Enums.OrderType)invocation.Arguments[2];
                    capturedPrice = (decimal?)invocation.Arguments[4]; // price is the 5th parameter
                    capturedTif = (Aster.Net.Enums.TimeInForce?)invocation.Arguments[6];
                }))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 1, AveragePrice = 3500m, QuantityFilled = 0.1m }));

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

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5);

        capturedOrderType.Should().Be(Aster.Net.Enums.OrderType.Limit, "should use Limit order for slippage protection");
        capturedTif.Should().Be(Aster.Net.Enums.TimeInForce.ImmediateOrCancel, "should use IOC time-in-force");
        // For Buy side: limitPrice = markPrice * 1.005 = 3500 * 1.005 = 3517.50
        capturedPrice.Should().Be(Math.Round(3500m * 1.005m, 2, MidpointRounding.AwayFromZero));
    }
}
