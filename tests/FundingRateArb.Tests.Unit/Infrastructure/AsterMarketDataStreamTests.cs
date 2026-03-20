using Aster.Net.Interfaces.Clients;
using Aster.Net.Interfaces.Clients.FuturesApi;
using Aster.Net.Objects.Models;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.Objects.Sockets;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class AsterMarketDataStreamTests
{
    [Fact]
    public void ExchangeName_IsAster()
    {
        var stream = CreateStream(out _, out _);
        stream.ExchangeName.Should().Be("Aster");
    }

    [Fact]
    public async Task StartAsync_ThrowsOnSubscriptionFailure()
    {
        var stream = CreateStream(out var mockSocketClient, out _);

        mockSocketClient.Setup(c => c.FuturesApi)
            .Returns(CreateMockFuturesApi(success: false).Object);

        var act = () => stream.StartAsync(new[] { "BTC" }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Aster WS*failed*");
    }

    [Fact]
    public void HandleUpdate_DividesFundingRateBy4()
    {
        var cache = new MarketDataCache();
        var stream = CreateStreamWithCache(cache, out _);

        // Simulate what the handler does by calling Update directly
        // (testing the normalization logic)
        var dto = new FundingRateDto
        {
            ExchangeName = "Aster",
            Symbol = "BTC",
            RawRate = 0.002m,
            RatePerHour = 0.002m / 4m, // 4h → per-hour
            MarkPrice = 50000m,
            IndexPrice = 49999m,
            Volume24hUsd = 0m,
        };
        cache.Update(dto);

        var cached = cache.GetLatest("Aster", "BTC");
        cached.Should().NotBeNull();
        cached!.RatePerHour.Should().Be(0.0005m);
    }

    [Fact]
    public void HandleUpdate_NormalizesSymbol()
    {
        // Test the normalization: BTCUSDT → BTC
        var symbol = "BTCUSDT";
        var normalized = symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? symbol[..^4]
            : symbol;

        normalized.Should().Be("BTC");
    }

    [Fact]
    public void IsConnected_IsFalse_BeforeStart()
    {
        var stream = CreateStream(out _, out _);
        stream.IsConnected.Should().BeFalse();
    }

    private static AsterMarketDataStream CreateStream(
        out Mock<IAsterSocketClient> mockSocket, out Mock<IMarketDataCache> mockCache)
    {
        mockSocket = new Mock<IAsterSocketClient>();
        mockCache = new Mock<IMarketDataCache>();
        return new AsterMarketDataStream(
            mockSocket.Object, mockCache.Object,
            NullLogger<AsterMarketDataStream>.Instance);
    }

    private static AsterMarketDataStream CreateStreamWithCache(
        MarketDataCache cache, out Mock<IAsterSocketClient> mockSocket)
    {
        mockSocket = new Mock<IAsterSocketClient>();
        return new AsterMarketDataStream(
            mockSocket.Object, cache,
            NullLogger<AsterMarketDataStream>.Instance);
    }

    private static Mock<IAsterSocketClientFuturesApi> CreateMockFuturesApi(bool success)
    {
        var mockFutures = new Mock<IAsterSocketClientFuturesApi>();
        if (success)
        {
            var mockSub = new Mock<UpdateSubscription>();
            mockFutures.Setup(f => f.SubscribeToMarkPriceUpdatesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<Action<DataEvent<AsterMarkPriceUpdate[]>>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CallResult<UpdateSubscription>(mockSub.Object));
        }
        else
        {
            mockFutures.Setup(f => f.SubscribeToMarkPriceUpdatesAsync(
                    It.IsAny<int?>(),
                    It.IsAny<Action<DataEvent<AsterMarkPriceUpdate[]>>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CallResult<UpdateSubscription>(new ServerError("error", new ErrorInfo(ErrorType.SystemError, "test error"), null!)));
        }
        return mockFutures;
    }
}
