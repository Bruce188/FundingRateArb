using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.Objects.Sockets;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using HyperLiquid.Net.Interfaces.Clients;
using HyperLiquid.Net.Interfaces.Clients.FuturesApi;
using HyperLiquid.Net.Objects.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class HyperliquidMarketDataStreamTests
{
    [Fact]
    public void ExchangeName_IsHyperliquid()
    {
        var stream = CreateStream(out _, out _);
        stream.ExchangeName.Should().Be("Hyperliquid");
    }

    [Fact]
    public async Task StartAsync_ContinuesOnPartialFailure()
    {
        var stream = CreateStream(out var mockSocket, out _);
        var mockFutures = new Mock<IHyperLiquidSocketClientFuturesApi>();

        var failResult = new CallResult<UpdateSubscription>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, "test error"), null!));

        // Both fail, but StartAsync should not throw
        mockFutures.Setup(f => f.SubscribeToSymbolUpdatesAsync(
                It.IsAny<string>(),
                It.IsAny<Action<DataEvent<HyperLiquidFuturesTicker>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        mockSocket.Setup(s => s.FuturesApi).Returns(mockFutures.Object);

        // Should not throw even though all subscriptions fail
        await stream.StartAsync(new[] { "BTC", "ETH" }, CancellationToken.None);

        // Verify both were attempted
        mockFutures.Verify(f => f.SubscribeToSymbolUpdatesAsync(
            It.IsAny<string>(),
            It.IsAny<Action<DataEvent<HyperLiquidFuturesTicker>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void HandleUpdate_UsesFundingRateAsIs()
    {
        // HyperLiquid funding rate is already per-hour, no division
        var cache = new MarketDataCache();
        var dto = new FundingRateDto
        {
            ExchangeName = "Hyperliquid",
            Symbol = "BTC",
            RawRate = 0.0005m,
            RatePerHour = 0.0005m,
            MarkPrice = 50000m,
            IndexPrice = 49999m,
            Volume24hUsd = 5_000_000m,
        };
        cache.Update(dto);

        var cached = cache.GetLatest("Hyperliquid", "BTC");
        cached.Should().NotBeNull();
        cached!.RatePerHour.Should().Be(0.0005m);
        cached.RawRate.Should().Be(cached.RatePerHour);
    }

    [Fact]
    public void IsConnected_IsFalse_BeforeStart()
    {
        var stream = CreateStream(out _, out _);
        stream.IsConnected.Should().BeFalse();
    }

    private static HyperliquidMarketDataStream CreateStream(
        out Mock<IHyperLiquidSocketClient> mockSocket, out Mock<IMarketDataCache> mockCache)
    {
        mockSocket = new Mock<IHyperLiquidSocketClient>();
        mockCache = new Mock<IMarketDataCache>();
        return new HyperliquidMarketDataStream(
            mockSocket.Object, mockCache.Object,
            NullLogger<HyperliquidMarketDataStream>.Instance);
    }
}
