using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Aster.Net.Interfaces.Clients;
using Aster.Net.Interfaces.Clients.FuturesApi;
using Aster.Net.Interfaces.Clients.FuturesV3Api;
using Aster.Net.Objects;
using Aster.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task GetAvailableBalance_EmptyResponse_ReturnsZero()
    {
        var balanceResult = SuccessBalances([]);

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

        available.Should().Be(0m);
    }

    [Fact]
    public async Task GetAvailableBalance_NoRecognizedAsset_ThrowsWithAssetList()
    {
        var balances = new[]
        {
            new AsterBalance { Asset = "BNB", AvailableBalance = 10m },
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

        var act = () => sut.GetAvailableBalanceAsync();

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*No recognized quote asset*");
        ex.WithMessage("*BNB*");
    }

    [Fact]
    public async Task GetAvailableBalance_UsdcFallback_ReturnsUsdcBalance()
    {
        var balances = new[]
        {
            new AsterBalance { Asset = "USDC", AvailableBalance = 38.49m },
            new AsterBalance { Asset = "BTC", AvailableBalance = 1.0m },
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

        available.Should().Be(38.49m,
            "USDC should be used as fallback when no USDT entries exist");
    }

    [Fact]
    public async Task GetAvailableBalance_UsdFallback_ReturnsUsdBalance()
    {
        var balances = new[]
        {
            new AsterBalance { Asset = "USD", AvailableBalance = 50m },
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

        available.Should().Be(50m,
            "USD should be used as fallback when no USDT or USDC entries exist");
    }

    [Fact]
    public async Task GetAvailableBalance_BothUsdcAndUsd_PrefersUsdc()
    {
        var balances = new[]
        {
            new AsterBalance { Asset = "USDC", AvailableBalance = 25m },
            new AsterBalance { Asset = "USD", AvailableBalance = 75m },
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

        available.Should().Be(25m,
            "USDC should take priority over USD in the fallback chain");
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
    public void RoundToTickSize_MidpointValue_RoundsAwayFromZero()
    {
        var method = typeof(AsterConnector).GetMethod("RoundToTickSize",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("RoundToTickSize must exist as a private static method");

        // 3517.25 / 0.5 = 7034.5 — exact midpoint. AwayFromZero rounds to 7035.
        var result = (decimal)method!.Invoke(null, new object[] { 3517.25m, 0.5m })!;

        result.Should().Be(3517.5m,
            "midpoint values must round away from zero (not banker's rounding)");
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

    // ── PlaceMarketOrderByQuantityAsync ──────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrderByQuantity_RoundsToExchangePrecision()
    {
        // Verify that quantity is rounded to qtyPrecision (default 3 when exchange info not loaded)
        // Input quantity 0.14286 should be rounded to 0.142 (ToZero with 3 decimals)
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

        // Pre-computed quantity = 0.14286, default precision is 3 (exchange info not loaded)
        await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, quantity: 0.14286m, leverage: 5);

        capturedQuantity.Should().NotBeNull("quantity must be passed to PlaceOrderAsync");
        capturedQuantity.Should().Be(0.142m, "0.14286 rounded ToZero at precision 3 = 0.142");
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantity_BelowMinNotional_ReturnsFalse()
    {
        // quantity=0.001 * markPrice=3500 = $3.50 < $5 minimum
        var client = BuildClientWithOrderResult(SuccessOrder(new AsterOrder { Id = 1 }));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, quantity: 0.001m, leverage: 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("below Aster minimum");
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_ExchangeReturnsZeroFill_ReturnsFailure()
    {
        // Simulates the production failure mode from positions #13/14/15: IOC limit order
        // expired without fills, or server-side min-notional rejection returned as
        // Success=true with QuantityFilled=0. The connector must surface this as a
        // failure instead of letting the ExecutionEngine mark the leg as filled.
        var zeroFillOrder = SuccessOrder(new AsterOrder
        {
            Id = 12345,
            AveragePrice = 3500m,
            QuantityFilled = 0m,
        });
        var client = BuildClientWithOrderResult(zeroFillOrder);
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        // quantity=0.01 * markPrice=3500 = $35 notional clears the $5 minimum guard.
        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, quantity: 0.01m, leverage: 5);

        result.Success.Should().BeFalse(
            "zero-fill response must not be reported as success; downstream ExecutionEngine relies on this flag");
        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Error!.Should().Contain("zero fill",
            "the error should clearly identify the zero-fill failure mode for operators and downstream code");
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_ExchangeReturnsPositiveFill_ReturnsSuccess()
    {
        // Regression guard: the new zero-fill check must not break the happy path.
        var filledOrder = SuccessOrder(new AsterOrder
        {
            Id = 12345,
            AveragePrice = 3500m,
            QuantityFilled = 0.01m,
        });
        var client = BuildClientWithOrderResult(filledOrder);
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, quantity: 0.01m, leverage: 5);

        result.Success.Should().BeTrue();
        result.FilledQuantity.Should().Be(0.01m);
        result.FilledPrice.Should().Be(3500m);
    }

    // ── GetQuantityPrecisionAsync (public interface) ────────────────────────────

    [Fact]
    public async Task GetQuantityPrecision_ReturnsDefaultWhenExchangeInfoNotLoaded()
    {
        // When exchange info fetch is not performed, default precision is 3
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([]));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var precision = await sut.GetQuantityPrecisionAsync("ETH");

        precision.Should().Be(3, "default fallback precision is 3 when exchange info is unavailable");
    }

    // ── Task 5.1: GetSymbolConstraintsAsync ─────────────────────────────────────

    private static WebCallResult<AsterExchangeInfo> SuccessExchangeInfo(AsterExchangeInfo data)
        => new WebCallResult<AsterExchangeInfo>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    private static WebCallResult<AsterExchangeInfo> FailExchangeInfo(string message)
        => new WebCallResult<AsterExchangeInfo>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, message), null!));

    private static AsterExchangeInfo BuildExchangeInfoWithMaxNotional(
        string symbol,
        decimal maxNotional,
        decimal minQty = 1m,
        decimal stepSize = 1m)
        => new AsterExchangeInfo
        {
            Symbols = new[]
            {
                new AsterSymbol
                {
                    Name = symbol,
                    Filters = new AsterSymbolFilter[]
                    {
                        new AsterSymbolMaxNotionalFilter { MaxNotional = maxNotional },
                        new AsterSymbolLotSizeFilter
                        {
                            MinQuantity = minQty,
                            StepSize = stepSize,
                            MaxQuantity = 1_000_000m,
                        },
                    },
                },
            },
        };

    /// <summary>
    /// Builds a fully wired Aster client with both GetMarkPricesAsync and GetExchangeInfoAsync,
    /// so tests of GetSymbolConstraintsAsync can mock exchange info responses.
    /// </summary>
    private static (Mock<IAsterRestClient> client,
                    Mock<IAsterRestClientFuturesApiExchangeData> exchangeData)
        BuildClientWithExchangeInfo(WebCallResult<AsterExchangeInfo> exchangeInfoResult)
    {
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(exchangeInfoResult);
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([]));
        exchangeDataMock
            .Setup(x => x.GetTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTickers([]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        return (clientMock, exchangeDataMock);
    }

    [Fact]
    public async Task AsterConnector_GetSymbolConstraints_ReturnsMaxNotional_FromExchangeInfo()
    {
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 1_000_000m);
        var (client, _) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var constraints = await sut.GetSymbolConstraintsAsync("WLFIUSDT");

        constraints.Should().NotBeNull();
        constraints.Symbol.Should().Be("WLFIUSDT");
        constraints.MaxNotionalValue.Should().Be(1_000_000m);
    }

    [Fact]
    public async Task AsterConnector_GetSymbolConstraints_CachesPerSymbol_WithTtl()
    {
        // Two calls within the TTL must hit the exchangeInfo endpoint only once.
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 1_000_000m);
        var (client, exchangeData) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.GetSymbolConstraintsAsync("WLFIUSDT");
        await sut.GetSymbolConstraintsAsync("WLFIUSDT");

        exchangeData.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "second call within TTL must be served from cache");
    }

    [Fact]
    public async Task AsterConnector_GetSymbolConstraints_TtlExpired_RefreshesFromExchange()
    {
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 1_000_000m);
        var (client, exchangeData) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        await sut.GetSymbolConstraintsAsync("WLFIUSDT");

        // Force TTL expiry via reflection (no IClock abstraction in the connector yet).
        var expiryField = typeof(AsterConnector)
            .GetField("_symbolConstraintsExpiry", BindingFlags.NonPublic | BindingFlags.Instance);
        expiryField.Should().NotBeNull("constraints expiry field is required for Task 5.1");
        expiryField!.SetValue(sut, DateTime.UtcNow.AddMinutes(-1));

        await sut.GetSymbolConstraintsAsync("WLFIUSDT");

        exchangeData.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "after TTL expiry the connector must refresh from the exchange");
    }

    [Fact]
    public async Task AsterConnector_GetSymbolConstraints_RefreshFails_ReturnsCachedValue()
    {
        // First call succeeds and populates the cache. Then the next refresh throws —
        // the connector must return the previously-cached value instead of propagating.
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 750_000m);
        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .SetupSequence(x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessExchangeInfo(info))
            .ThrowsAsync(new InvalidOperationException("simulated network failure"));
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);
        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);

        var sut = new AsterConnector(clientMock.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var first = await sut.GetSymbolConstraintsAsync("WLFIUSDT");
        first.MaxNotionalValue.Should().Be(750_000m);

        // Force TTL expiry so the next call attempts a refresh.
        var expiryField = typeof(AsterConnector)
            .GetField("_symbolConstraintsExpiry", BindingFlags.NonPublic | BindingFlags.Instance);
        expiryField!.SetValue(sut, DateTime.UtcNow.AddMinutes(-1));

        var second = await sut.GetSymbolConstraintsAsync("WLFIUSDT");

        second.MaxNotionalValue.Should().Be(750_000m, "refresh failed — cached value must survive");
    }

    // ── NT2: Single-flight concurrency ─────────────────────────────────────────

    [Fact]
    public async Task GetSymbolConstraintsAsync_ConcurrentCallers_OnlyOneRefreshOccurs()
    {
        // Arrange
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 500_000m);
        var (client, exchangeData) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        // Expire the TTL so all concurrent callers hit the refresh path.
        typeof(AsterConnector)
            .GetField("_symbolConstraintsExpiry", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sut, DateTime.UtcNow.AddMinutes(-1));

        // Act — 50 concurrent callers
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => sut.GetSymbolConstraintsAsync("WLFIUSDT", CancellationToken.None)))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert — exactly one HTTP call despite 50 callers
        exchangeData.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once(),
            "single-flight must ensure only one HTTP refresh fires regardless of concurrent callers");
    }

    // ── NT3: Negative sentinel test ────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolConstraintsAsync_UnknownSymbol_ReturnsSentinelWithMaxNotional()
    {
        // Arrange — exchange info only contains BTCUSDT, not UNKNOWNSYM
        var info = BuildExchangeInfoWithMaxNotional("BTCUSDT", maxNotional: 2_000_000m);
        var (client, _) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        // Act
        var result = await sut.GetSymbolConstraintsAsync("UNKNOWNSYM");

        // Assert — no-cap sentinel returned for unknown symbol
        result.MaxNotionalValue.Should().Be(decimal.MaxValue,
            "unknown symbol must return sentinel with MaxNotionalValue == decimal.MaxValue");
        result.StepSize.Should().Be(0m,
            "unknown symbol sentinel must have StepSize == 0");
    }

    // ── NT4: Cache-cap test ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSymbolConstraintsAsync_CacheAtCap_DoesNotAddNewEntry()
    {
        // Arrange — pre-fill the cache with exactly 10 000 entries
        var info = BuildExchangeInfoWithMaxNotional("NEWSYM", maxNotional: 100_000m);
        var (client, _) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var cache = (ConcurrentDictionary<string, AsterSymbolConstraints>)
            typeof(AsterConnector)
                .GetField("_symbolConstraintsCache", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(sut)!;

        var placeholder = new AsterSymbolConstraints
        {
            Symbol = "placeholder",
            MaxNotionalValue = 1m,
            MinQuantity = 1m,
            StepSize = 1m,
        };

        for (var i = 0; i < 10_000; i++)
        {
            cache.TryAdd($"SYM{i}", placeholder);
        }

        cache.Count.Should().Be(10_000, "precondition: cache must be full before the test call");

        // Act — call with a symbol NOT already in the cache
        var result = await sut.GetSymbolConstraintsAsync("NEWSYM");

        // Assert — cache count unchanged; no new entry written; no exception
        cache.Count.Should().Be(10_000,
            "cache at cap must not grow when a new symbol entry would be written");
        result.Should().NotBeNull("a result (sentinel or real) must always be returned");
    }

    // ── Symbol normalization (profitability-fixes F2) ──────────────────────────

    [Fact]
    public async Task AsterConnector_GetSymbolConstraints_ResolvesNormalizedSymbol()
    {
        // Production callers (ExchangeSymbolConstraintsProvider → SignalEngine) pass the
        // normalized symbol "WLFI". The Aster exchangeInfo payload keys symbols as
        // "WLFIUSDT". Before the fix, the lookup missed and returned a no-cap sentinel,
        // disabling every downstream notional / lot-size validation.
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 1_500_000m);
        var (client, _) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var constraints = await sut.GetSymbolConstraintsAsync("WLFI");

        constraints.Should().NotBeNull();
        constraints.MaxNotionalValue.Should().Be(1_500_000m,
            "the normalized form 'WLFI' must resolve against exchangeInfo rows written as 'WLFIUSDT'");
    }

    [Fact]
    public async Task AsterConnector_GetSymbolConstraints_RawAndNormalizedReturnSameValue()
    {
        // Backwards-compat: existing tests and any straggler callers still pass "WLFIUSDT".
        // Both forms must resolve to the same cached constraints.
        var info = BuildExchangeInfoWithMaxNotional("WLFIUSDT", maxNotional: 1_500_000m);
        var (client, _) = BuildClientWithExchangeInfo(SuccessExchangeInfo(info));
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var raw = await sut.GetSymbolConstraintsAsync("WLFIUSDT");
        var normalized = await sut.GetSymbolConstraintsAsync("WLFI");

        raw.MaxNotionalValue.Should().Be(1_500_000m);
        normalized.MaxNotionalValue.Should().Be(1_500_000m,
            "normalized form must hit the same cache slot as raw form");
    }

    // ── V3 API Surface Routing ────────────────────────────────────────────────

    /// <summary>
    /// Builds a mock IAsterRestClient with both FuturesApi and FuturesV3Api wired.
    /// FuturesApi.ExchangeData is always needed for mark price lookups (public calls stay on V1).
    /// </summary>
    private static (Mock<IAsterRestClient> Client,
                     Mock<IAsterRestClientFuturesApiTrading> V1Trading,
                     Mock<IAsterRestClientFuturesV3ApiTrading> V3Trading,
                     Mock<IAsterRestClientFuturesApiAccount> V1Account,
                     Mock<IAsterRestClientFuturesV3ApiAccount> V3Account,
                     Mock<IAsterRestClientFuturesApiExchangeData> V1ExchangeData,
                     Mock<IAsterRestClientFuturesV3ApiExchangeData> V3ExchangeData)
        BuildDualSurfaceClient(decimal markPrice = 3500m)
    {
        // V1 mocks
        var v1TradingMock = new Mock<IAsterRestClientFuturesApiTrading>();
        v1TradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 1, QuantityFilled = 0.1m }));
        v1TradingMock
            .Setup(x => x.GetPositionsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessPositions([new AsterPosition { Symbol = "ETHUSDT", PositionAmount = 0.5m }]));

        var v1AccountMock = new Mock<IAsterRestClientFuturesApiAccount>();
        v1AccountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());
        v1AccountMock
            .Setup(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessBalances([new AsterBalance { Asset = "USDT", AvailableBalance = 1000m }]));

        var exchangeDataMock = new Mock<IAsterRestClientFuturesApiExchangeData>();
        exchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", markPrice, markPrice - 5m, 0.0001m)]));
        exchangeDataMock
            .Setup(x => x.GetTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTickers([]));

        var futuresApiMock = new Mock<IAsterRestClientFuturesApi>();
        futuresApiMock.SetupGet(f => f.Trading).Returns(v1TradingMock.Object);
        futuresApiMock.SetupGet(f => f.Account).Returns(v1AccountMock.Object);
        futuresApiMock.SetupGet(f => f.ExchangeData).Returns(exchangeDataMock.Object);

        // V3 mocks
        var v3TradingMock = new Mock<IAsterRestClientFuturesV3ApiTrading>();
        v3TradingMock
            .Setup(x => x.PlaceOrderAsync(
                It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
                It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
                It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
                It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
                It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder(new AsterOrder { Id = 2, QuantityFilled = 0.1m }));
        v3TradingMock
            .Setup(x => x.GetPositionsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessPositions([new AsterPosition { Symbol = "ETHUSDT", PositionAmount = 0.5m }]));

        var v3AccountMock = new Mock<IAsterRestClientFuturesV3ApiAccount>();
        v3AccountMock
            .Setup(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessLeverage());
        v3AccountMock
            .Setup(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessBalances([new AsterBalance { Asset = "USDT", AvailableBalance = 2000m }]));

        var v3ExchangeDataMock = new Mock<IAsterRestClientFuturesV3ApiExchangeData>();
        v3ExchangeDataMock
            .Setup(x => x.GetMarkPricesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessMarkPrices([MakeMarkPrice("ETHUSDT", markPrice, markPrice - 5m, 0.0001m)]));
        v3ExchangeDataMock
            .Setup(x => x.GetTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessTickers([]));

        var v3ApiMock = new Mock<IAsterRestClientFuturesV3Api>();
        v3ApiMock.SetupGet(a => a.Trading).Returns(v3TradingMock.Object);
        v3ApiMock.SetupGet(a => a.Account).Returns(v3AccountMock.Object);
        v3ApiMock.SetupGet(a => a.ExchangeData).Returns(v3ExchangeDataMock.Object);

        // Client with both surfaces
        var clientMock = new Mock<IAsterRestClient>();
        clientMock.SetupGet(c => c.FuturesApi).Returns(futuresApiMock.Object);
        clientMock.SetupGet(c => c.FuturesV3Api).Returns(v3ApiMock.Object);

        return (clientMock, v1TradingMock, v3TradingMock, v1AccountMock, v3AccountMock, exchangeDataMock, v3ExchangeDataMock);
    }

    [Fact]
    public async Task PlaceMarketOrderAsync_WithV3Mode_UsesFuturesV3ApiTrading()
    {
        var (client, v1Trading, v3Trading, _, _, _, _) = BuildDualSurfaceClient();
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: true);

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        result.Success.Should().BeTrue();
        v3Trading.Verify(x => x.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
            It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
            It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once, "V3 mode must route PlaceOrderAsync through FuturesV3Api.Trading");
        v1Trading.Verify(x => x.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
            It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
            It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never, "V3 mode must not call FuturesApi.Trading.PlaceOrderAsync");
    }

    [Fact]
    public async Task GetBalancesAsync_WithV3Mode_UsesFuturesV3ApiAccount()
    {
        var (client, _, _, v1Account, v3Account, _, _) = BuildDualSurfaceClient();
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: true);

        var balance = await sut.GetAvailableBalanceAsync();

        balance.Should().Be(2000m, "V3 Account mock returns 2000 USDT");
        v3Account.Verify(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once,
            "V3 mode must route GetBalancesAsync through FuturesV3Api.Account");
        v1Account.Verify(x => x.GetBalancesAsync(It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never,
            "V3 mode must not call FuturesApi.Account.GetBalancesAsync");
    }

    [Fact]
    public async Task ClosePositionAsync_WithV3Mode_UsesFuturesV3ApiTrading()
    {
        var (client, v1Trading, v3Trading, _, _, _, _) = BuildDualSurfaceClient();
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: true);

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        v3Trading.Verify(x => x.GetPositionsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once,
            "V3 mode must route GetPositionsAsync through FuturesV3Api.Trading");
        v3Trading.Verify(x => x.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
            It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
            It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once, "V3 close must use FuturesV3Api.Trading.PlaceOrderAsync");
        v1Trading.Verify(x => x.GetPositionsAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never,
            "V3 mode must not call FuturesApi.Trading.GetPositionsAsync");
    }

    [Fact]
    public async Task PlaceMarketOrderAsync_WithV1Mode_UsesFuturesApiTrading()
    {
        var (client, v1Trading, v3Trading, _, _, _, _) = BuildDualSurfaceClient();
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: false);

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        result.Success.Should().BeTrue();
        v1Trading.Verify(x => x.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
            It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
            It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once, "V1 mode must route through FuturesApi.Trading");
        v3Trading.Verify(x => x.PlaceOrderAsync(
            It.IsAny<string>(), It.IsAny<Aster.Net.Enums.OrderSide>(), It.IsAny<Aster.Net.Enums.OrderType>(),
            It.IsAny<decimal?>(), It.IsAny<decimal?>(), It.IsAny<Aster.Net.Enums.PositionSide?>(),
            It.IsAny<Aster.Net.Enums.TimeInForce?>(), It.IsAny<bool?>(), It.IsAny<string>(),
            It.IsAny<decimal?>(), It.IsAny<bool?>(), It.IsAny<decimal?>(), It.IsAny<decimal?>(),
            It.IsAny<Aster.Net.Enums.WorkingType?>(), It.IsAny<bool?>(), It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never, "V1 mode must not call FuturesV3Api.Trading");
    }

    [Fact]
    public async Task SetLeverageAsync_WithV3Mode_UsesFuturesV3ApiAccount()
    {
        var (client, _, _, v1Account, v3Account, _, _) = BuildDualSurfaceClient();
        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: true);

        // PlaceMarketOrderAsync calls SetLeverageAsync internally
        await sut.PlaceMarketOrderAsync("ETH", Side.Long, 175m, 5);

        v3Account.Verify(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Once,
            "V3 mode must route SetLeverageAsync through FuturesV3Api.Account");
        v1Account.Verify(x => x.SetLeverageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()), Times.Never,
            "V3 mode must not call FuturesApi.Account.SetLeverageAsync");
    }

    [Fact]
    public async Task GetQuantityPrecision_WithV3Mode_UsesFuturesV3ApiExchangeData()
    {
        // Build a dual-surface client with V3 exchange data wired for GetExchangeInfoAsync
        var (client, _, _, _, _, _, _) = BuildDualSurfaceClient();

        // Set up V3 exchange data to return valid exchange info
        var v3ExchangeDataMock = new Mock<IAsterRestClientFuturesV3ApiExchangeData>();
        v3ExchangeDataMock
            .Setup(x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessExchangeInfo(new AsterExchangeInfo
            {
                Symbols = [new AsterSymbol { Name = "ETHUSDT", QuantityPrecision = 2, PricePrecision = 2 }],
            }));

        var v3ApiMock = Mock.Get(client.Object.FuturesV3Api);
        v3ApiMock.SetupGet(a => a.ExchangeData).Returns(v3ExchangeDataMock.Object);

        // V1 exchange data should NOT be called for exchange info
        var v1ExchangeDataMock = Mock.Get(client.Object.FuturesApi.ExchangeData);

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: true);

        var precision = await sut.GetQuantityPrecisionAsync("ETH");

        precision.Should().Be(2, "V3 exchange info should provide the precision");
        v3ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "V3 mode must route GetExchangeInfoAsync through FuturesV3Api.ExchangeData");
        v1ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "V3 mode must not call FuturesApi.ExchangeData.GetExchangeInfoAsync");
    }

    [Fact]
    public async Task GetQuantityPrecision_WithV1Mode_UsesFuturesApiExchangeData()
    {
        var (client, _, _, _, _, _, _) = BuildDualSurfaceClient();

        // Set up V1 exchange data to return valid exchange info
        var v1ExchangeDataMock = Mock.Get(client.Object.FuturesApi.ExchangeData);
        v1ExchangeDataMock
            .Setup(x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessExchangeInfo(new AsterExchangeInfo
            {
                Symbols = [new AsterSymbol { Name = "ETHUSDT", QuantityPrecision = 4, PricePrecision = 2 }],
            }));

        var v3ExchangeDataMock = Mock.Get(client.Object.FuturesV3Api.ExchangeData);

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: false);

        var precision = await sut.GetQuantityPrecisionAsync("ETH");

        precision.Should().Be(4, "V1 exchange info should provide the precision");
        v1ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "V1 mode must route GetExchangeInfoAsync through FuturesApi.ExchangeData");
        v3ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "V1 mode must not call FuturesV3Api.ExchangeData.GetExchangeInfoAsync");
    }

    // ── Factory V3 Detection ──────────────────────────────────────────────────

    private static ExchangeConnectorFactory BuildFactoryForAsterTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMarkPriceCache, SingletonMarkPriceCache>();
        services.AddHttpClient();

        var mockProvider = new Mock<ResiliencePipelineProvider<string>>();
        mockProvider.Setup(p => p.GetPipeline(It.IsAny<string>())).Returns(ResiliencePipeline.Empty);
        services.AddSingleton(mockProvider.Object);

        var sp = services.BuildServiceProvider();
        var factoryLogger = sp.GetRequiredService<ILogger<ExchangeConnectorFactory>>();
        return new ExchangeConnectorFactory(sp, factoryLogger, Mock.Of<IDydxConnectorFactory>());
    }

    /// <summary>
    /// Reads the private _useV3Api field from an AsterConnector via reflection.
    /// </summary>
    private static bool GetUseV3Api(AsterConnector connector)
    {
        var field = typeof(AsterConnector).GetField("_useV3Api", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("AsterConnector must have a _useV3Api field");
        return (bool)field!.GetValue(connector)!;
    }

    [Fact]
    public void CreateAsterConnector_WithV3Credentials_PassesUseV3ApiTrue()
    {
        var factory = BuildFactoryForAsterTests();
        var walletAddress = "0x" + new string('a', 64);
        var privateKey = "0x" + new string('b', 64);

        var connector = factory.CreateAsterConnector(
            apiKey: null, apiSecret: null,
            walletAddress: walletAddress, privateKey: privateKey);

        connector.Should().NotBeNull("V3 credentials must produce a non-null connector");
        factory.LastAsterCredentials.Should().NotBeNull();
        factory.LastAsterCredentials!.V3.Should().NotBeNull(
            "wallet+key pair must select the V3 credential path");
        GetUseV3Api(connector!).Should().BeTrue(
            "factory must pass useV3Api=true when V3 credentials are detected");
    }

    [Fact]
    public void CreateAsterConnector_WithBothV1AndV3Credentials_PrefersV3()
    {
        var factory = BuildFactoryForAsterTests();
        var walletAddress = "0x" + new string('a', 64);
        var privateKey = "0x" + new string('b', 64);

        var connector = factory.CreateAsterConnector(
            apiKey: "testkey", apiSecret: "testsecret",
            walletAddress: walletAddress, privateKey: privateKey);

        connector.Should().NotBeNull(
            "when both V1 and V3 credentials are supplied, V3 takes precedence");
        factory.LastAsterCredentials.Should().NotBeNull();
        factory.LastAsterCredentials!.V3.Should().NotBeNull(
            "when both credential types are present the factory must prefer V3 over V1");
        GetUseV3Api(connector!).Should().BeTrue(
            "factory must pass useV3Api=true when V3 credentials take precedence over V1");
    }

    // ── V3 routing for GetSymbolConstraintsAsync ──────────────────────────────

    [Fact]
    public async Task GetSymbolConstraints_WithV3Mode_UsesFuturesV3ApiExchangeData()
    {
        var (client, _, _, _, _, _, _) = BuildDualSurfaceClient();

        var v3ExchangeDataMock = new Mock<IAsterRestClientFuturesV3ApiExchangeData>();
        v3ExchangeDataMock
            .Setup(x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessExchangeInfo(new AsterExchangeInfo
            {
                Symbols = [new AsterSymbol { Name = "ETHUSDT", QuantityPrecision = 3, PricePrecision = 2, Filters = [] }],
            }));

        var v3ApiMock = Mock.Get(client.Object.FuturesV3Api);
        v3ApiMock.SetupGet(a => a.ExchangeData).Returns(v3ExchangeDataMock.Object);

        var v1ExchangeDataMock = Mock.Get(client.Object.FuturesApi.ExchangeData);

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: true);

        var constraints = await sut.GetSymbolConstraintsAsync("ETH");

        constraints.Should().NotBeNull();
        v3ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "V3 mode must route constraints refresh through FuturesV3Api.ExchangeData");
        v1ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "V3 mode must not call FuturesApi.ExchangeData.GetExchangeInfoAsync for constraints");
    }

    [Fact]
    public async Task GetSymbolConstraints_WithV1Mode_UsesFuturesApiExchangeData()
    {
        var (client, _, _, _, _, _, _) = BuildDualSurfaceClient();

        var v1ExchangeDataMock = Mock.Get(client.Object.FuturesApi.ExchangeData);
        v1ExchangeDataMock
            .Setup(x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessExchangeInfo(new AsterExchangeInfo
            {
                Symbols = [new AsterSymbol { Name = "ETHUSDT", QuantityPrecision = 3, PricePrecision = 2, Filters = [] }],
            }));

        var v3ExchangeDataMock = Mock.Get(client.Object.FuturesV3Api.ExchangeData);

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache(), useV3Api: false);

        var constraints = await sut.GetSymbolConstraintsAsync("ETH");

        constraints.Should().NotBeNull();
        v1ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "V1 mode must route constraints refresh through FuturesApi.ExchangeData");
        v3ExchangeDataMock.Verify(
            x => x.GetExchangeInfoAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "V1 mode must not call FuturesV3Api.ExchangeData.GetExchangeInfoAsync for constraints");
    }

    // ── Variable funding-interval detection ────────────────────────────────────

    private static WebCallResult<AsterFundingInfo[]> SuccessFundingInfo(AsterFundingInfo[] data)
        => new WebCallResult<AsterFundingInfo[]>(
            HttpStatusCode.OK, null, null, null, null, null, null, null, null,
            null, null, ResultDataSource.Server, data, null);

    private static AsterFundingInfo MakeFundingInfo(string symbol, int intervalHours)
        => new AsterFundingInfo
        {
            Symbol = symbol,
            FundingIntervalHours = intervalHours,
        };

    [Fact]
    public async Task GetFundingRatesAsync_WithFundingInfo_PropagatesDetectedIntervalPerSymbol()
    {
        // Two symbols with divergent funding intervals: BTCUSDT=8h, ETHUSDT=4h.
        // The connector must surface DetectedFundingIntervalHours per symbol and
        // divide the raw 8h/4h rate by the matching interval to produce RatePerHour.
        var prices = new[]
        {
            MakeMarkPrice("BTCUSDT", 65000m, 64980m, fundingRate: 0.0008m),
            MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var exchangeDataMock = Mock.Get(client.Object.FuturesApi.ExchangeData);
        exchangeDataMock
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessFundingInfo([
                MakeFundingInfo("BTCUSDT", 8),
                MakeFundingInfo("ETHUSDT", 4),
            ]));

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        var btc = rates.Single(r => r.Symbol == "BTC");
        btc.DetectedFundingIntervalHours.Should().Be(8);
        btc.RatePerHour.Should().Be(0.0008m / 8m,
            "BTCUSDT reports 8h interval — RatePerHour should divide the raw 8h rate by 8");

        var eth = rates.Single(r => r.Symbol == "ETH");
        eth.DetectedFundingIntervalHours.Should().Be(4);
        eth.RatePerHour.Should().Be(0.0004m / 4m,
            "ETHUSDT reports 4h interval — RatePerHour should divide the raw 4h rate by 4");
    }

    [Fact]
    public async Task GetFundingRatesAsync_WhenFundingInfoFails_FallsBackToEightHour()
    {
        // Mirrors the Binance resilience contract: a funding-info outage must not
        // crash the fetch. The connector should still return rates with
        // DetectedFundingIntervalHours=null and the 8h default divisor.
        var prices = new[]
        {
            MakeMarkPrice("ETHUSDT", 3500m, 3495m, fundingRate: 0.0004m),
            MakeMarkPrice("BTCUSDT", 65000m, 64980m, fundingRate: 0.0008m),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices(prices));
        var exchangeDataMock = Mock.Get(client.Object.FuturesApi.ExchangeData);
        exchangeDataMock
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("funding info endpoint offline"));

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        rates.Should().HaveCount(2);
        rates.Should().AllSatisfy(r => r.DetectedFundingIntervalHours.Should().BeNull(
            "funding info failed — interval should not be reported"));

        var eth = rates.Single(r => r.Symbol == "ETH");
        eth.RatePerHour.Should().Be(0.00005m, "ETH should fall back to the 8h divisor (0.0004 / 8)");

        var btc = rates.Single(r => r.Symbol == "BTC");
        btc.RatePerHour.Should().Be(0.0001m, "BTC should fall back to the 8h divisor (0.0008 / 8)");
    }

    // ── Normalization audit: per-symbol interval pin tests ────────────────────

    [Fact]
    public async Task GetFundingRates_FundingInfo4h_DividesByFourAndSurfacesDetectedInterval()
    {
        // Phase 2 target: per-4h symbol whose FundingIntervalHours=4 is returned by
        // GetFundingInfoAsync. RatePerHour must be rawRate/4 and
        // DetectedFundingIntervalHours must equal 4.
        var now = DateTime.UtcNow;
        var mp = new AsterMarkPrice
        {
            Symbol = "ETHUSDT",
            MarkPrice = 3500m,
            IndexPrice = 3495m,
            FundingRate = -0.000542m,
            NextFundingTime = now.AddHours(1),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([mp]));
        Mock.Get(client.Object.FuturesApi.ExchangeData)
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessFundingInfo([MakeFundingInfo("ETHUSDT", 4)]));

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        var dto = rates.Should().ContainSingle().Which;
        dto.RawRate.Should().Be(-0.000542m);
        dto.DetectedFundingIntervalHours.Should().Be(4);
        dto.RatePerHour.Should().Be(-0.000542m / 4m,
            "4h interval: raw rate must be divided by 4");
        dto.RatePerHour.Should().Be(dto.RawRate / (decimal)dto.DetectedFundingIntervalHours!.Value,
            "invariant: RatePerHour == RawRate / DetectedFundingIntervalHours");
    }

    [Fact]
    public async Task GetFundingRates_FundingInfo8h_DividesByEightAndSurfacesDetectedInterval()
    {
        // Phase 2 target: per-8h symbol whose FundingIntervalHours=8 is returned by
        // GetFundingInfoAsync. RatePerHour must be rawRate/8 and
        // DetectedFundingIntervalHours must equal 8.
        var now = DateTime.UtcNow;
        var mp = new AsterMarkPrice
        {
            Symbol = "BTCUSDT",
            MarkPrice = 65000m,
            IndexPrice = 64980m,
            FundingRate = -0.000542m,
            NextFundingTime = now.AddHours(1),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([mp]));
        Mock.Get(client.Object.FuturesApi.ExchangeData)
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessFundingInfo([MakeFundingInfo("BTCUSDT", 8)]));

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        var dto = rates.Should().ContainSingle().Which;
        dto.RawRate.Should().Be(-0.000542m);
        dto.DetectedFundingIntervalHours.Should().Be(8);
        dto.RatePerHour.Should().Be(-0.000542m / 8m,
            "8h interval: raw rate must be divided by 8");
        dto.RatePerHour.Should().Be(dto.RawRate / (decimal)dto.DetectedFundingIntervalHours!.Value,
            "invariant: RatePerHour == RawRate / DetectedFundingIntervalHours");
    }

    [Fact]
    public async Task GetFundingRates_FundingInfoMissingForSymbol_InfersIntervalFromNextFundingTime()
    {
        // Phase 2 target: GetFundingInfoAsync returns no entry for this symbol.
        // Connector must infer the interval from NextFundingTime (now+30m places the
        // settlement near a 4h boundary, so the inferred interval must be 4).
        // Currently fails because the connector defaults to the 8h fallback.
        var now = DateTime.UtcNow;
        var mp = new AsterMarkPrice
        {
            Symbol = "ETHUSDT",
            MarkPrice = 3500m,
            IndexPrice = 3495m,
            FundingRate = -0.000542m,
            NextFundingTime = now.AddMinutes(30),
        };
        var client = BuildClientWithMarkPrices(SuccessMarkPrices([mp]));
        Mock.Get(client.Object.FuturesApi.ExchangeData)
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessFundingInfo([])); // funding info present but no entry for this symbol

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), BuildNullLogger(), new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        var dto = rates.Should().ContainSingle().Which;
        dto.DetectedFundingIntervalHours.Should().Be(4,
            "NextFundingTime ~30m away maps to the 4h cycle via nearest-plausible-interval inference");
        dto.RatePerHour.Should().Be(dto.RawRate / (decimal)dto.DetectedFundingIntervalHours!.Value,
            "invariant: RatePerHour == RawRate / DetectedFundingIntervalHours");
    }

    [Fact]
    public async Task GetFundingRates_FundingInfoThrows_EmitsInferableDtosAndSkipsUninferableWithWarning()
    {
        // Phase 2 target: GetFundingInfoAsync throws a transient error.
        // - Symbols with a NextFundingTime that allows interval inference → DTO emitted,
        //   DetectedFundingIntervalHours set to the inferred value (never 8 = old fallback).
        // - Symbols with NextFundingTime=null (uninferrable) → skipped entirely;
        //   a warning containing the symbol name must be logged.
        var now = DateTime.UtcNow;
        var loggerMock = new Mock<ILogger<AsterConnector>>();

        var mpInferable = new AsterMarkPrice
        {
            Symbol = "ETHUSDT",
            MarkPrice = 3500m,
            IndexPrice = 3495m,
            FundingRate = -0.000542m,
            NextFundingTime = now.AddMinutes(30), // near 4h boundary → inferable
        };
        var mpUninferrable = new AsterMarkPrice
        {
            Symbol = "BTCUSDT",
            MarkPrice = 65000m,
            IndexPrice = 64980m,
            FundingRate = 0.0001m,
            NextFundingTime = default, // DateTime.MinValue → implausibly far in past, uninferrable
        };

        var client = BuildClientWithMarkPrices(SuccessMarkPrices([mpInferable, mpUninferrable]));
        Mock.Get(client.Object.FuturesApi.ExchangeData)
            .Setup(x => x.GetFundingInfoAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("funding info endpoint offline"));

        var sut = new AsterConnector(client.Object, BuildEmptyPipelineProvider(), loggerMock.Object, new SingletonMarkPriceCache());

        var rates = await sut.GetFundingRatesAsync();

        // ETH has a NextFundingTime — interval is inferable; DTO must be emitted
        rates.Should().Contain(r => r.Symbol == "ETH",
            "ETH NextFundingTime allows interval inference — DTO must still be emitted");
        var eth = rates.Single(r => r.Symbol == "ETH");
        eth.DetectedFundingIntervalHours.Should().NotBeNull(
            "inferred interval must be surfaced even when funding info is unavailable");
        eth.DetectedFundingIntervalHours.Should().NotBe(8,
            "8h is the old blind fallback — an inferred value must differ from it");

        // BTC has DateTime.MinValue NextFundingTime — uninferrable; connector must skip it
        rates.Should().NotContain(r => r.Symbol == "BTC",
            "BTC NextFundingTime is null — connector must skip it and emit no DTO");

        // A warning containing the skipped symbol name must be logged
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("BTCUSDT")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "a warning containing the skipped symbol name must be logged");

        // No DTO may carry DetectedFundingIntervalHours=8 (that is the old blind fallback)
        rates.Should().NotContain(r => r.DetectedFundingIntervalHours == 8,
            "no emitted DTO should use the 8h default when funding info is unavailable");
    }
}
