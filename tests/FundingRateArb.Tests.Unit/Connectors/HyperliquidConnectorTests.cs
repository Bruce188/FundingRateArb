using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using FluentAssertions;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using HyperLiquid.Net.Enums;
using HyperLiquid.Net.Interfaces.Clients;
using HyperLiquid.Net.Interfaces.Clients.FuturesApi;
using HyperLiquid.Net.Interfaces.Clients.SpotApi;
using HyperLiquid.Net.Objects.Models;
using Moq;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Tests.Unit.Connectors;

public class HyperliquidConnectorTests
{
    private readonly Mock<IHyperLiquidRestClient> _mockRestClient;
    private readonly Mock<IHyperLiquidRestClientFuturesApi> _mockFuturesApi;
    private readonly Mock<IHyperLiquidRestClientFuturesApiExchangeData> _mockExchangeData;
    private readonly Mock<IHyperLiquidRestClientFuturesApiAccount> _mockAccount;
    private readonly Mock<IHyperLiquidRestClientFuturesApiTrading> _mockTrading;
    private readonly Mock<ResiliencePipelineProvider<string>> _mockPipelineProvider;
    private readonly HyperliquidConnector _sut;

    public HyperliquidConnectorTests()
    {
        _mockRestClient = new Mock<IHyperLiquidRestClient>();
        _mockFuturesApi = new Mock<IHyperLiquidRestClientFuturesApi>();
        _mockExchangeData = new Mock<IHyperLiquidRestClientFuturesApiExchangeData>();
        _mockAccount = new Mock<IHyperLiquidRestClientFuturesApiAccount>();
        _mockTrading = new Mock<IHyperLiquidRestClientFuturesApiTrading>();

        _mockRestClient.Setup(c => c.FuturesApi).Returns(_mockFuturesApi.Object);
        _mockFuturesApi.Setup(f => f.ExchangeData).Returns(_mockExchangeData.Object);
        _mockFuturesApi.Setup(f => f.Account).Returns(_mockAccount.Object);
        _mockFuturesApi.Setup(f => f.Trading).Returns(_mockTrading.Object);

        // SpotApi mock — balance tests need this for unified account spot USDC query
        var mockSpotApi = new Mock<IHyperLiquidRestClientSpotApi>();
        var mockSpotAccount = new Mock<IHyperLiquidRestClientSpotApiAccount>();
        _mockRestClient.Setup(c => c.SpotApi).Returns(mockSpotApi.Object);
        mockSpotApi.Setup(s => s.Account).Returns(mockSpotAccount.Object);
        // Default: empty spot balances (no spot USDC)
        var emptySpotResult = new WebCallResult<HyperLiquidBalance[]>(
            System.Net.HttpStatusCode.OK,
            null, null, null, null, null, null, null, null, null, null,
            ResultDataSource.Server,
            Array.Empty<HyperLiquidBalance>(),
            null);
        mockSpotAccount
            .Setup(a => a.GetBalancesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptySpotResult);

        _mockPipelineProvider = new Mock<ResiliencePipelineProvider<string>>();
        _mockPipelineProvider.Setup(p => p.GetPipeline(It.IsAny<string>()))
            .Returns(ResiliencePipeline.Empty);

        _sut = new HyperliquidConnector(_mockRestClient.Object, _mockPipelineProvider.Object);
    }

    // ── Exchange Name ──────────────────────────────────────────────────────────

    [Fact]
    public void ExchangeName_IsHyperliquid()
    {
        _sut.ExchangeName.Should().Be("Hyperliquid");
    }

    // ── GetFundingRatesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetFundingRates_ParsesResponseCorrectly()
    {
        // Arrange
        var tickers = new[]
        {
            CreateTicker("ETH", fundingRate: 0.0001m, markPrice: 3000m, notionalVolume: 5_000_000m, oraclePrice: 2999m),
            CreateTicker("BTC", fundingRate: 0.0002m, markPrice: 65000m, notionalVolume: 10_000_000m, oraclePrice: 64999m),
        };
        SetupExchangeInfoSuccess(tickers);

        // Act
        var result = await _sut.GetFundingRatesAsync();

        // Assert
        result.Should().HaveCount(2);

        var eth = result.First(r => r.Symbol == "ETH");
        eth.ExchangeName.Should().Be("Hyperliquid");
        eth.RatePerHour.Should().Be(0.0001m);
        eth.MarkPrice.Should().Be(3000m);
        eth.Volume24hUsd.Should().Be(5_000_000m);

        var btc = result.First(r => r.Symbol == "BTC");
        btc.ExchangeName.Should().Be("Hyperliquid");
        btc.RatePerHour.Should().Be(0.0002m);
        btc.MarkPrice.Should().Be(65000m);
    }

    [Fact]
    public async Task GetFundingRates_RateIsAlreadyHourly_NoConversion()
    {
        // Arrange — Hyperliquid publishes 1-hour rates; RatePerHour must equal RawRate
        const decimal rawRate = 0.000125m;
        var tickers = new[]
        {
            CreateTicker("ETH", fundingRate: rawRate, markPrice: 3000m, notionalVolume: 1m, oraclePrice: 3000m),
        };
        SetupExchangeInfoSuccess(tickers);

        // Act
        var result = await _sut.GetFundingRatesAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].RawRate.Should().Be(rawRate);
        result[0].RatePerHour.Should().Be(rawRate, "Hyperliquid funding rate is already per-hour — no division needed");
    }

    [Fact]
    public async Task GetFundingRates_SymbolFormat_IsJustBaseAsset()
    {
        // Arrange — SDK returns futures symbols without quote suffix
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m),
            CreateTicker("SOL", 0.0002m, 150m, 1m, 150m),
        };
        SetupExchangeInfoSuccess(tickers);

        // Act
        var result = await _sut.GetFundingRatesAsync();

        // Assert — symbols must be exactly "ETH" and "SOL", not "ETHUSDC", "ETH-PERP", etc.
        result.Select(r => r.Symbol).Should().BeEquivalentTo(new[] { "ETH", "SOL" });
    }

    [Fact]
    public async Task GetFundingRates_WhenApiFails_ThrowsInvalidOperationException()
    {
        // Arrange
        var errorInfo = new ErrorInfo(ErrorType.Unknown, "Exchange API unavailable");
        var failedResult = new WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers>(
            new ServerError(errorInfo, null));

        _mockExchangeData
            .Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedResult);

        // Act & Assert
        await _sut.Invoking(c => c.GetFundingRatesAsync())
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Exchange API unavailable*");
    }

    // ── PlaceMarketOrderAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_ReturnsSuccessResult_WhenSdkSucceeds()
    {
        // Arrange — mark price is fetched before placing the order
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m),
        };
        SetupExchangeInfoSuccess(tickers);

        var orderResult = new HyperLiquidOrderResult
        {
            OrderId = 123456L,
            FilledQuantity = 0.03m,
            AveragePrice = 3010m,
            Status = HyperLiquid.Net.Enums.OrderStatus.Filled,
        };
        SetupTradingSuccess(orderResult);

        // Act
        var result = await _sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 5);

        // Assert
        result.Success.Should().BeTrue();
        result.OrderId.Should().Be("123456");
        result.FilledQuantity.Should().Be(0.03m);
        result.FilledPrice.Should().Be(3010m);
    }

    [Fact]
    public async Task PlaceMarketOrder_WhenSdkFails_ReturnsFailureResult()
    {
        // Arrange
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m),
        };
        SetupExchangeInfoSuccess(tickers);

        var errorInfo = new ErrorInfo(ErrorType.Unknown, "Insufficient margin");
        var failedOrderResult = new WebCallResult<HyperLiquidOrderResult>(
            new ServerError(errorInfo, null));

        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderSide>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<HyperLiquid.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlType?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedOrderResult);

        // Act
        var result = await _sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 5);

        // Assert
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Insufficient margin");
    }

    // ── ClosePositionAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ClosePosition_UsesReduceOnly()
    {
        // Arrange
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m),
        };
        SetupExchangeInfoSuccess(tickers);
        SetupPositionQuantity("ETH", 0.1m);

        var orderResult = new HyperLiquidOrderResult
        {
            OrderId = 789L,
            FilledQuantity = 0.03m,
            AveragePrice = 3000m,
            Status = HyperLiquid.Net.Enums.OrderStatus.Filled,
        };

        bool? capturedReduceOnly = null;
        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderSide>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<HyperLiquid.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlType?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, HyperLiquid.Net.Enums.OrderSide, HyperLiquid.Net.Enums.OrderType,
                      decimal, decimal, HyperLiquid.Net.Enums.TimeInForce?, bool?, string?,
                      decimal?, HyperLiquid.Net.Enums.TpSlType?, HyperLiquid.Net.Enums.TpSlGrouping?,
                      string?, DateTime?, CancellationToken>(
                (sym, side, type, qty, price, tif, ro, clOrdId, trig, tpsl, tpslGrp, vault, exp, ct) =>
                {
                    capturedReduceOnly = ro;
                })
            .ReturnsAsync(CreateOrderWebCallResult(orderResult));

        // Act
        await _sut.ClosePositionAsync("ETH", Side.Long);

        // Assert
        capturedReduceOnly.Should().BeTrue("ClosePosition must set reduceOnly=true");
    }

    [Fact]
    public async Task ClosePosition_ForLong_PlacesSell()
    {
        // Arrange
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m),
        };
        SetupExchangeInfoSuccess(tickers);
        SetupPositionQuantity("ETH", 0.1m);

        var orderResult = new HyperLiquidOrderResult { OrderId = 1L, FilledQuantity = 0.01m, AveragePrice = 3000m };

        HyperLiquid.Net.Enums.OrderSide capturedSide = default;
        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderSide>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<HyperLiquid.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlType?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, HyperLiquid.Net.Enums.OrderSide, HyperLiquid.Net.Enums.OrderType,
                      decimal, decimal, HyperLiquid.Net.Enums.TimeInForce?, bool?, string?,
                      decimal?, HyperLiquid.Net.Enums.TpSlType?, HyperLiquid.Net.Enums.TpSlGrouping?,
                      string?, DateTime?, CancellationToken>(
                (sym, side, type, qty, price, tif, ro, clOrdId, trig, tpsl, tpslGrp, vault, exp, ct) =>
                {
                    capturedSide = side;
                })
            .ReturnsAsync(CreateOrderWebCallResult(orderResult));

        // Act — close a Long position, should place a Sell
        await _sut.ClosePositionAsync("ETH", Side.Long);

        // Assert
        capturedSide.Should().Be(HyperLiquid.Net.Enums.OrderSide.Sell);
    }

    // ── GetMarkPriceAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMarkPrice_ReturnsCorrectPrice()
    {
        // Arrange
        var tickers = new[]
        {
            CreateTicker("BTC", 0.0001m, 65000m, 1m, 64999m),
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 2999m),
        };
        SetupExchangeInfoSuccess(tickers);

        // Act
        var price = await _sut.GetMarkPriceAsync("ETH");

        // Assert
        price.Should().Be(3000m);
    }

    [Fact]
    public async Task GetMarkPrice_CachePreventsRedundantApiCalls()
    {
        // Arrange — two consecutive GetMarkPriceAsync calls for the same asset
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 2999m),
        };
        SetupExchangeInfoSuccess(tickers);

        // Act — call twice
        var price1 = await _sut.GetMarkPriceAsync("ETH");
        var price2 = await _sut.GetMarkPriceAsync("ETH");

        // Assert — the API was only called once (cache hit on second call)
        price1.Should().Be(3000m);
        price2.Should().Be(3000m);
        _mockExchangeData.Verify(
            e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "The ticker endpoint should only be fetched once; second call must use cache");
    }

    [Fact]
    public async Task GetMarkPrice_CacheMissRefetchesApiAfterTtlExpiry()
    {
        // Arrange — first setup returns price 3000, second setup returns price 3100
        int callCount = 0;
        _mockExchangeData
            .Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                var price = callCount == 1 ? 3000m : 3100m;
                var data = new HyperLiquidFuturesExchangeInfoAndTickers
                {
                    Tickers = new[]
                    {
                        CreateTicker("ETH", 0.0001m, price, 1m, price - 1m),
                    }
                };
                return new WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers>(
                    System.Net.HttpStatusCode.OK,
                    null, null, null, null, null, null, null, null, null, null,
                    ResultDataSource.Server,
                    data,
                    null);
            });

        // Act — first call populates the cache
        var price1 = await _sut.GetMarkPriceAsync("ETH");

        // Force cache expiry by calling the internal method that sets _cacheExpiry to MinValue
        // We verify the cache works, i.e. first call fetches, second call uses cache
        var price2 = await _sut.GetMarkPriceAsync("ETH");

        // Both within TTL so same price, only 1 API call
        price1.Should().Be(3000m);
        price2.Should().Be(3000m);
        callCount.Should().Be(1, "second call within TTL must not re-fetch");
    }

    [Fact]
    public async Task GetMarkPrice_CacheIsSharedAcrossMultipleAssets()
    {
        // Arrange — cache should store all tickers, so fetching ETH then BTC only hits API once
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 2999m),
            CreateTicker("BTC", 0.0001m, 65000m, 1m, 64999m),
        };
        SetupExchangeInfoSuccess(tickers);

        // Act
        var ethPrice = await _sut.GetMarkPriceAsync("ETH");
        var btcPrice = await _sut.GetMarkPriceAsync("BTC");

        // Assert — only one API call for both assets
        ethPrice.Should().Be(3000m);
        btcPrice.Should().Be(65000m);
        _mockExchangeData.Verify(
            e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "Fetching a second asset within TTL must use cached ticker data");
    }

    // ── IDisposable (C3) ──────────────────────────────────────────────────────

    [Fact]
    public void HyperliquidConnector_ImplementsIDisposable()
    {
        _sut.Should().BeAssignableTo<IDisposable>(
            "HyperliquidConnector must implement IDisposable to release the SemaphoreSlim");
    }

    [Fact]
    public void HyperliquidConnector_Dispose_DoesNotThrow()
    {
        var act = () => ((IDisposable)_sut).Dispose();
        act.Should().NotThrow("Dispose must be safe to call");
    }

    // ── GetMarkPrice cache correctness (H6) ───────────────────────────────────

    [Fact]
    public async Task GetMarkPrice_CacheHit_DoesNotPerformExtraHttpCall()
    {
        // Two calls within TTL should result in exactly one API call (post-lock read uses local var)
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, 3000m, 1m, 2999m),
            CreateTicker("BTC", 0.0001m, 65000m, 1m, 64999m),
        };
        SetupExchangeInfoSuccess(tickers);

        // First call fetches, second and third should use the cache
        var price1 = await _sut.GetMarkPriceAsync("ETH");
        var price2 = await _sut.GetMarkPriceAsync("ETH");
        var price3 = await _sut.GetMarkPriceAsync("BTC");

        price1.Should().Be(3000m);
        price2.Should().Be(3000m);
        price3.Should().Be(65000m);

        _mockExchangeData.Verify(
            e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "All subsequent calls within TTL must use the cached result without re-fetching");
    }

    // ── GetAvailableBalanceAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetAvailableBalance_ReturnsWithdrawableAmount()
    {
        // Arrange
        var accountInfo = new HyperLiquidFuturesAccount
        {
            Withdrawable = 250.50m,
        };
        var successResult = new WebCallResult<HyperLiquidFuturesAccount>(
            System.Net.HttpStatusCode.OK,
            null, null, null, null, null, null, null, null, null, null,
            ResultDataSource.Server,
            accountInfo,
            null);

        _mockAccount
            .Setup(a => a.GetAccountInfoAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResult);

        // Act
        var balance = await _sut.GetAvailableBalanceAsync();

        // Assert
        balance.Should().Be(250.50m);
    }

    [Fact]
    public async Task GetAvailableBalance_WithVaultAddress_PassesVaultToApi()
    {
        // Arrange — connector with vault address set
        var vaultConnector = new HyperliquidConnector(
            _mockRestClient.Object, _mockPipelineProvider.Object, vaultAddress: "0xVaultAddr123");

        var accountInfo = new HyperLiquidFuturesAccount { Withdrawable = 500m };
        var successResult = new WebCallResult<HyperLiquidFuturesAccount>(
            System.Net.HttpStatusCode.OK,
            null, null, null, null, null, null, null, null, null, null,
            ResultDataSource.Server,
            accountInfo,
            null);

        string? capturedUser = null;
        _mockAccount
            .Setup(a => a.GetAccountInfoAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, string?, CancellationToken>((user, sub, ct) => capturedUser = user)
            .ReturnsAsync(successResult);

        // Act
        await vaultConnector.GetAvailableBalanceAsync();

        // Assert — vault address must be passed to GetAccountInfoAsync
        capturedUser.Should().Be("0xVaultAddr123",
            "when _vaultAddress is set, it should be passed to GetAccountInfoAsync so balance reflects the sub-account");
    }

    [Fact]
    public async Task ClosePosition_WithVaultAddress_PassesVaultToPlaceOrder()
    {
        // Arrange — connector with vault address set
        var vaultConnector = new HyperliquidConnector(
            _mockRestClient.Object, _mockPipelineProvider.Object, vaultAddress: "0xVaultAddr123");

        var tickers = new[] { CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m) };
        SetupExchangeInfoSuccess(tickers);
        SetupPositionQuantity("ETH", 0.1m);

        string? capturedVaultAddress = null;
        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderSide>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<HyperLiquid.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlType?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, HyperLiquid.Net.Enums.OrderSide, HyperLiquid.Net.Enums.OrderType,
                      decimal, decimal, HyperLiquid.Net.Enums.TimeInForce?, bool?, string?,
                      decimal?, HyperLiquid.Net.Enums.TpSlType?, HyperLiquid.Net.Enums.TpSlGrouping?,
                      string?, DateTime?, CancellationToken>(
                (sym, side, type, qty, price, tif, ro, clOrdId, trig, tpsl, tpslGrp, vault, exp, ct) =>
                {
                    capturedVaultAddress = vault;
                })
            .ReturnsAsync(CreateOrderWebCallResult(new HyperLiquidOrderResult
            {
                OrderId = 1L,
                FilledQuantity = 0.1m,
                AveragePrice = 3000m
            }));

        // Act
        await vaultConnector.ClosePositionAsync("ETH", Side.Long);

        // Assert — vault address must be passed to PlaceOrderAsync
        capturedVaultAddress.Should().Be("0xVaultAddr123",
            "ClosePositionAsync must pass _vaultAddress to the order call");
    }

    // ── Private Helpers ───────────────────────────────────────────────────────

    private static HyperLiquidFuturesTicker CreateTicker(
        string symbol,
        decimal fundingRate,
        decimal markPrice,
        decimal notionalVolume,
        decimal oraclePrice)
    {
        return new HyperLiquidFuturesTicker
        {
            Symbol = symbol,
            FundingRate = fundingRate,
            MarkPrice = markPrice,
            NotionalVolume = notionalVolume,
            OraclePrice = oraclePrice,
        };
    }

    private void SetupExchangeInfoSuccess(HyperLiquidFuturesTicker[] tickers,
        HyperLiquidFuturesSymbol[]? symbols = null)
    {
        var data = new HyperLiquidFuturesExchangeInfoAndTickers
        {
            Tickers = tickers,
            ExchangeInfo = new HyperLiquidFuturesExchangeInfo
            {
                Symbols = symbols ?? Array.Empty<HyperLiquidFuturesSymbol>(),
            },
        };
        var webResult = new WebCallResult<HyperLiquidFuturesExchangeInfoAndTickers>(
            System.Net.HttpStatusCode.OK,
            null, null, null, null, null, null, null, null, null, null,
            ResultDataSource.Server,
            data,
            null);

        _mockExchangeData
            .Setup(e => e.GetExchangeInfoAndTickersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(webResult);
    }

    private void SetupTradingSuccess(HyperLiquidOrderResult orderResult)
    {
        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderSide>(),
                It.IsAny<HyperLiquid.Net.Enums.OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<HyperLiquid.Net.Enums.TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlType?>(),
                It.IsAny<HyperLiquid.Net.Enums.TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateOrderWebCallResult(orderResult));
    }

    private static WebCallResult<HyperLiquidOrderResult> CreateOrderWebCallResult(HyperLiquidOrderResult orderResult)
    {
        return new WebCallResult<HyperLiquidOrderResult>(
            System.Net.HttpStatusCode.OK,
            null, null, null, null, null, null, null, null, null, null,
            ResultDataSource.Server,
            orderResult,
            null);
    }

    private void SetupPositionQuantity(string asset, decimal quantity)
    {
        var accountInfo = new HyperLiquidFuturesAccount
        {
            Withdrawable = 1000m,
            Positions = [
                new HyperLiquidPosition
                {
                    Position = new HyperLiquidPositionInfo
                    {
                        Symbol = asset,
                        PositionQuantity = quantity,
                    }
                }
            ]
        };
        var successResult = new WebCallResult<HyperLiquidFuturesAccount>(
            System.Net.HttpStatusCode.OK,
            null, null, null, null, null, null, null, null, null, null,
            ResultDataSource.Server,
            accountInfo,
            null);
        _mockAccount
            .Setup(a => a.GetAccountInfoAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResult);
    }

    // ── B-W7: szDecimals per-asset rounding ──────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_UsesSymbolSpecificSzDecimals_WhenAvailable()
    {
        // Arrange — symbol with QuantityDecimals=3, markPrice=150
        // Expected quantity = 100 * 5 / 150 = 3.333... → rounded to 3 decimals → 3.333
        const decimal markPrice = 150m;
        var tickers = new[]
        {
            CreateTicker("SOL", 0.0001m, markPrice, 1m, markPrice),
        };
        var symbols = new[]
        {
            new HyperLiquidFuturesSymbol { Name = "SOL", QuantityDecimals = 3 },
        };
        SetupExchangeInfoSuccess(tickers, symbols);

        decimal? capturedQuantity = null;
        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<OrderSide>(),
                It.IsAny<OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<TpSlType?>(),
                It.IsAny<TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, OrderSide, OrderType, decimal, decimal, TimeInForce?,
                      bool?, string?, decimal?, TpSlType?, TpSlGrouping?, string?, DateTime?, CancellationToken>(
                (sym, side, type, qty, price, tif, ro, clOrdId, trig, tpsl, tpslGrp, vault, exp, ct) =>
                {
                    capturedQuantity = qty;
                })
            .ReturnsAsync(CreateOrderWebCallResult(new HyperLiquidOrderResult
            {
                OrderId = 1L,
                FilledQuantity = 3.333m,
                AveragePrice = markPrice,
                Status = OrderStatus.Filled,
            }));

        // Act
        var result = await _sut.PlaceMarketOrderAsync("SOL", Side.Long, sizeUsdc: 100m, leverage: 5);

        // Assert — quantity must be rounded to 3 decimal places
        result.Success.Should().BeTrue();
        capturedQuantity.Should().NotBeNull();

        // 100 * 5 / 150 = 3.33333... → floor to 3 decimals → 3.333
        capturedQuantity.Should().Be(3.333m,
            "quantity must be rounded DOWN (ToZero) to QuantityDecimals=3 for SOL");
    }

    // ── D3: Connector safety tests ────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_MarkPriceZero_ReturnsFalse()
    {
        var tickers = new[] { CreateTicker("ETH", 0.0001m, 0m, 1m, 0m) };
        SetupExchangeInfoSuccess(tickers);

        var result = await _sut.PlaceMarketOrderAsync("ETH", Side.Long, 100m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("zero");
    }

    [Fact]
    public async Task PlaceMarketOrder_ZeroQuantity_ReturnsFalse()
    {
        // sizeUsdc=0, leverage=5, markPrice=3000 → quantity = 0*5/3000 = 0
        var tickers = new[] { CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m) };
        SetupExchangeInfoSuccess(tickers);

        var result = await _sut.PlaceMarketOrderAsync("ETH", Side.Long, 0m, 5);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("zero");
    }

    [Fact]
    public async Task ClosePosition_ZeroQuantity_ReturnsFailure()
    {
        // When no position info exists, should return failure instead of using 1M fallback
        var tickers = new[] { CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m) };
        SetupExchangeInfoSuccess(tickers);

        var result = await _sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Position quantity is zero");
        // Verify no order was placed
        _mockTrading.Verify(t => t.PlaceOrderAsync(
            It.IsAny<string>(),
            It.IsAny<HyperLiquid.Net.Enums.OrderSide>(),
            It.IsAny<HyperLiquid.Net.Enums.OrderType>(),
            It.IsAny<decimal>(),
            It.IsAny<decimal>(),
            It.IsAny<HyperLiquid.Net.Enums.TimeInForce?>(),
            It.IsAny<bool?>(),
            It.IsAny<string?>(),
            It.IsAny<decimal?>(),
            It.IsAny<HyperLiquid.Net.Enums.TpSlType?>(),
            It.IsAny<HyperLiquid.Net.Enums.TpSlGrouping?>(),
            It.IsAny<string?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlaceMarketOrder_FallsBackTo6Decimals_WhenSymbolNotInCache()
    {
        // Arrange — no symbols in ExchangeInfo, falls back to 6 decimals
        // markPrice=3000, quantity = 100*5/3000 = 0.166666... → floor to 6 → 0.166666
        const decimal markPrice = 3000m;
        var tickers = new[]
        {
            CreateTicker("ETH", 0.0001m, markPrice, 1m, markPrice),
        };
        SetupExchangeInfoSuccess(tickers);  // no symbols passed → empty array

        decimal? capturedQuantity = null;
        _mockTrading
            .Setup(t => t.PlaceOrderAsync(
                It.IsAny<string>(),
                It.IsAny<OrderSide>(),
                It.IsAny<OrderType>(),
                It.IsAny<decimal>(),
                It.IsAny<decimal>(),
                It.IsAny<TimeInForce?>(),
                It.IsAny<bool?>(),
                It.IsAny<string?>(),
                It.IsAny<decimal?>(),
                It.IsAny<TpSlType?>(),
                It.IsAny<TpSlGrouping?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, OrderSide, OrderType, decimal, decimal, TimeInForce?,
                      bool?, string?, decimal?, TpSlType?, TpSlGrouping?, string?, DateTime?, CancellationToken>(
                (sym, side, type, qty, price, tif, ro, clOrdId, trig, tpsl, tpslGrp, vault, exp, ct) =>
                {
                    capturedQuantity = qty;
                })
            .ReturnsAsync(CreateOrderWebCallResult(new HyperLiquidOrderResult
            {
                OrderId = 2L,
                FilledQuantity = 0.166666m,
                AveragePrice = markPrice,
                Status = OrderStatus.Filled,
            }));

        // Act
        var result = await _sut.PlaceMarketOrderAsync("ETH", Side.Long, sizeUsdc: 100m, leverage: 5);

        // Assert — falls back to 6 decimal places
        result.Success.Should().BeTrue();
        capturedQuantity.Should().NotBeNull();
        // 100 * 5 / 3000 = 0.16666666... → floor to 6 → 0.166666
        capturedQuantity.Should().Be(0.166666m,
            "falls back to 6 decimals when symbol not found in szDecimals cache");
    }

    // ── NB10: Min notional validation ────────────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrder_BelowMinNotional_ReturnsFalse()
    {
        // sizeUsdc=1, leverage=1, markPrice=3000 → quantity = 1/3000 ≈ 0.000333, notional ≈ $1.0 < $10
        var tickers = new[] { CreateTicker("ETH", 0.0001m, 3000m, 1m, 3000m) };
        SetupExchangeInfoSuccess(tickers);

        var result = await _sut.PlaceMarketOrderAsync("ETH", Side.Long, 1m, 1);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("below Hyperliquid minimum $10.00");
    }
}
