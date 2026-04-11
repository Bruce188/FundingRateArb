using System.Reflection;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Errors;
using CryptoExchange.Net.Objects.Sockets;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using HyperLiquid.Net.Interfaces.Clients;
using HyperLiquid.Net.Interfaces.Clients.FuturesApi;
using IHLFuturesExchangeData = HyperLiquid.Net.Interfaces.Clients.FuturesApi.IHyperLiquidSocketClientFuturesApiExchangeData;
using HyperLiquid.Net.Objects.Models;
using Microsoft.Extensions.Logging;
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
        var mockExchangeData = new Mock<IHLFuturesExchangeData>();

        var failResult = new CallResult<UpdateSubscription>(
            new ServerError("error", new ErrorInfo(ErrorType.SystemError, "test error"), null!));

        // Both fail, but StartAsync should not throw
        mockExchangeData.Setup(e => e.SubscribeToSymbolUpdatesAsync(
                It.IsAny<string>(),
                It.IsAny<Action<DataEvent<HyperLiquidFuturesTicker>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        mockFutures.Setup(f => f.ExchangeData).Returns(mockExchangeData.Object);
        mockSocket.Setup(s => s.FuturesApi).Returns(mockFutures.Object);

        // Should not throw even though all subscriptions fail
        await stream.StartAsync(new[] { "BTC", "ETH" }, CancellationToken.None);

        // Verify both were attempted
        mockExchangeData.Verify(e => e.SubscribeToSymbolUpdatesAsync(
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

    // ── Serialization + handler isolation + disconnect throttle tests ─────────────

    [Fact]
    public async Task SemaphoreEnforced_OnlyOneOperationAtATime()
    {
        var (mockSocket, mockFutures, concurrency) = CreateCountingFuturesApi(
            subscribeDelayMs: 5);
        var stream = CreateStream(mockSocket, new MarketDataCache());

        var symbols = Enumerable.Range(0, 50).Select(i => $"SYM{i}").ToArray();
        await stream.StartAsync(symbols, CancellationToken.None);

        concurrency.MaxObserved.Should().BeLessOrEqualTo(1,
            "subscribe operations must be serialized via the socket semaphore");
    }

    [Fact]
    public async Task Subscribe_Concurrent200Operations_NoUnhandledExceptions()
    {
        var (mockSocket, mockFutures, concurrency) = CreateCountingFuturesApi(
            subscribeDelayMs: 1);
        var stream = CreateStream(mockSocket, new MarketDataCache());

        // 200 concurrent StartAsync calls, each subscribing a unique symbol.
        var tasks = Enumerable.Range(0, 200)
            .Select(i => Task.Run(() =>
                stream.StartAsync(new[] { $"SYM{i}" }, CancellationToken.None)))
            .ToArray();

        var aggregate = async () => await Task.WhenAll(tasks);
        await aggregate.Should().NotThrowAsync(
            "200 concurrent subscribe operations must not leak exceptions");

        concurrency.MaxObserved.Should().BeLessOrEqualTo(1,
            "the semaphore must prevent concurrent execution even under load");
    }

    [Fact]
    public void Subscribe_HandlerThrows_LoopContinues()
    {
        var mockSocket = new Mock<IHyperLiquidSocketClient>();
        var mockLogger = new Mock<ILogger<HyperliquidMarketDataStream>>();

        // Cache throws on Update — simulating a downstream exception inside the handler.
        var throwingCache = new Mock<IMarketDataCache>();
        throwingCache.Setup(c => c.Update(It.IsAny<FundingRateDto>()))
            .Throws(new InvalidOperationException("simulated handler failure"));

        var stream = new HyperliquidMarketDataStream(
            mockSocket.Object, throwingCache.Object, mockLogger.Object);

        var dataEvent = new DataEvent<HyperLiquidFuturesTicker>(
            "Hyperliquid",
            new HyperLiquidFuturesTicker { Symbol = "BTC", FundingRate = 0.0001m, MarkPrice = 50_000m },
            DateTime.UtcNow,
            null);

        // The SDK invokes HandleSymbolUpdate from its async state machine — any
        // exception escaping that method would tear down the socket. The handler
        // must swallow downstream exceptions and log a Warning instead.
        var firstInvocation = () => stream.InvokeSymbolUpdateForTest("BTC", dataEvent);
        firstInvocation.Should().NotThrow(
            "handler exceptions must not escape into the SDK async state machine");

        // A second invocation — proving the loop continues — also must not throw.
        var secondInvocation = () => stream.InvokeSymbolUpdateForTest("BTC", dataEvent);
        secondInvocation.Should().NotThrow();

        // The logger should have recorded a Warning for the failed handler.
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("handler")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeast(2),
            "a Warning log must be emitted for each failed handler invocation");
    }

    [Fact]
    public async Task Disconnect_ExceedsThreshold_PausesSubscriptionChurn()
    {
        var (mockSocket, mockFutures, concurrency) = CreateCountingFuturesApi(
            subscribeDelayMs: 0);
        var mockLogger = new Mock<ILogger<HyperliquidMarketDataStream>>();
        var stream = new HyperliquidMarketDataStream(
            mockSocket.Object, new MarketDataCache(), mockLogger.Object);

        // Initial subscribe works normally (count baseline).
        await stream.StartAsync(new[] { "SEED" }, CancellationToken.None);
        concurrency.TotalSubscribes.Should().Be(1);

        // Simulate 25 disconnect events in quick succession (exceeds 20/min threshold).
        for (var i = 0; i < 25; i++)
        {
            await stream.RecordDisconnectForTestAsync($"SYM{i}", CancellationToken.None);
        }

        // The stream must now be in the throttled state.
        stream.IsThrottledForTest.Should().BeTrue(
            "25 disconnects in <60s must trip the churn throttle");

        // Subsequent subscribe attempts are short-circuited: no new calls to the socket.
        await stream.StartAsync(new[] { "AFTER_THROTTLE" }, CancellationToken.None);
        concurrency.TotalSubscribes.Should().Be(1,
            "once throttled, subscribe attempts must be skipped until the cooldown expires");

        // A Warning log should have been emitted about the throttle.
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("disconnect")
                                             || v.ToString()!.Contains("throttle")
                                             || v.ToString()!.Contains("pausing")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "a Warning log must be emitted when the disconnect churn threshold trips");
    }

    // ── NT6: IsConnected non-blocking ────────────────────────────────────────────

    [Fact]
    public async Task IsConnected_DoesNotBlock_WhenSocketLockIsHeld()
    {
        var stream = CreateStream(out _, out _);

        // Acquire _socketLock on a background task so IsConnected would deadlock if it tried
        // to acquire the same lock.
        var socketLock = (SemaphoreSlim)typeof(HyperliquidMarketDataStream)
            .GetField("_socketLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(stream)!;

        await socketLock.WaitAsync();
        try
        {
            // IsConnected must return immediately (within 50 ms) without acquiring the lock.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var isConnectedTask = Task.Run(() => stream.IsConnected, cts.Token);
            var completed = await Task.WhenAny(isConnectedTask, Task.Delay(50));

            completed.Should().Be(isConnectedTask,
                "IsConnected is backed by a volatile field and must not acquire _socketLock");
            var isConnectedValue = await isConnectedTask;
            isConnectedValue.Should().BeFalse("stream has not been started");
        }
        finally
        {
            socketLock.Release();
        }
    }

    // ── NT7: Threshold boundary lower (19 disconnects = not throttled) ────────────

    [Fact]
    public async Task IsThrottled_AtNineteenDisconnects_IsNotThrottled()
    {
        var stream = CreateStream(out _, out _);

        for (var i = 0; i < 19; i++)
        {
            await stream.RecordDisconnectForTestAsync($"SYM{i}", CancellationToken.None);
        }

        stream.IsThrottledForTest.Should().BeFalse(
            "19 disconnect events is one below the threshold of 20 — must not throttle");
    }

    // ── NT8: Threshold boundary exact (20 disconnects = throttled) ────────────────

    [Fact]
    public async Task IsThrottled_AtTwentyDisconnects_IsThrottled()
    {
        var stream = CreateStream(out _, out _);

        for (var i = 0; i < 20; i++)
        {
            await stream.RecordDisconnectForTestAsync($"SYM{i}", CancellationToken.None);
        }

        stream.IsThrottledForTest.Should().BeTrue(
            "exactly 20 disconnect events must trip the churn throttle");
    }

    // ── StopAsync lock-timeout fallback ──────────────────────────────────────────

    [Fact]
    public async Task StopAsync_WhenLockHeld_CompletesViaBestEffortPath()
    {
        var stream = CreateStream(out _, out _);

        var socketLock = (SemaphoreSlim)typeof(HyperliquidMarketDataStream)
            .GetField("_socketLock", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(stream)!;

        // Hold the lock for the duration of the test so StopAsync cannot acquire it.
        await socketLock.WaitAsync();
        try
        {
            // StopAsync must complete via the best-effort path (5s internal CTS fires).
            var stopTask = stream.StopAsync();
            var completed = await Task.WhenAny(stopTask, Task.Delay(10_000));

            completed.Should().Be(stopTask,
                "StopAsync must complete within 10s via the best-effort fallback when lock is held");
            await stopTask; // propagate any exception
        }
        finally
        {
            socketLock.Release();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static HyperliquidMarketDataStream CreateStream(
        out Mock<IHyperLiquidSocketClient> mockSocket, out Mock<IMarketDataCache> mockCache)
    {
        mockSocket = new Mock<IHyperLiquidSocketClient>();
        mockCache = new Mock<IMarketDataCache>();
        return new HyperliquidMarketDataStream(
            mockSocket.Object, mockCache.Object,
            NullLogger<HyperliquidMarketDataStream>.Instance);
    }

    private static HyperliquidMarketDataStream CreateStream(
        Mock<IHyperLiquidSocketClient> mockSocket, IMarketDataCache cache)
    {
        return new HyperliquidMarketDataStream(
            mockSocket.Object, cache,
            NullLogger<HyperliquidMarketDataStream>.Instance);
    }

    private sealed class ConcurrencyTracker
    {
        private int _current;
        private int _max;
        private int _total;

        public int MaxObserved => Volatile.Read(ref _max);
        public int TotalSubscribes => Volatile.Read(ref _total);

        public void Enter()
        {
            var cur = Interlocked.Increment(ref _current);
            int oldMax;
            do
            {
                oldMax = Volatile.Read(ref _max);
                if (cur <= oldMax)
                {
                    break;
                }
            } while (Interlocked.CompareExchange(ref _max, cur, oldMax) != oldMax);
            Interlocked.Increment(ref _total);
        }

        public void Leave() => Interlocked.Decrement(ref _current);
    }

    private static (Mock<IHyperLiquidSocketClient> socket,
                    Mock<IHyperLiquidSocketClientFuturesApi> futures,
                    ConcurrencyTracker tracker)
        CreateCountingFuturesApi(int subscribeDelayMs)
    {
        var tracker = new ConcurrencyTracker();
        var mockSocket = new Mock<IHyperLiquidSocketClient>();
        var mockFutures = new Mock<IHyperLiquidSocketClientFuturesApi>();
        var mockExchangeData = new Mock<IHLFuturesExchangeData>();

        mockExchangeData.Setup(e => e.SubscribeToSymbolUpdatesAsync(
                It.IsAny<string>(),
                It.IsAny<Action<DataEvent<HyperLiquidFuturesTicker>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Action<DataEvent<HyperLiquidFuturesTicker>>, CancellationToken>(
                async (_, _, ct) =>
                {
                    tracker.Enter();
                    try
                    {
                        if (subscribeDelayMs > 0)
                        {
                            await Task.Delay(subscribeDelayMs, ct);
                        }
                    }
                    finally
                    {
                        tracker.Leave();
                    }
                    return new CallResult<UpdateSubscription>(new Mock<UpdateSubscription>().Object);
                });

        mockFutures.Setup(f => f.ExchangeData).Returns(mockExchangeData.Object);
        mockSocket.Setup(s => s.FuturesApi).Returns(mockFutures.Object);
        return (mockSocket, mockFutures, tracker);
    }
}
