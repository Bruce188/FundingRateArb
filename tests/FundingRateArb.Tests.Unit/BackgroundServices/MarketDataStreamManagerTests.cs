using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class MarketDataStreamManagerTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockScopeProvider = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext = new();
    private readonly Mock<IHubClients<IDashboardClient>> _mockHubClients = new();
    private readonly Mock<IDashboardClient> _mockDashboardClient = new();
    private readonly Mock<IExchangeSupportedSymbolsCache> _mockSymbolsCache = new();

    private static readonly List<Asset> ActiveAssets =
    [
        new Asset { Id = 1, Symbol = "ETH", IsActive = true },
        new Asset { Id = 2, Symbol = "BTC", IsActive = true },
    ];

    public MarketDataStreamManagerTests()
    {
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);

        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(ActiveAssets);

        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
        _mockHubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockDashboardClient.Object);

        // Default: cache returns all known symbols (no filtering)
        _mockSymbolsCache
            .Setup(c => c.GetSupportedSymbolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(ActiveAssets.Select(a => a.Symbol), StringComparer.OrdinalIgnoreCase));
    }

    private MarketDataStreamManager CreateSut(params IMarketDataStream[] streams)
        => CreateSut(_mockSymbolsCache.Object, streams);

    private MarketDataStreamManager CreateSut(
        IExchangeSupportedSymbolsCache symbolsCache,
        params IMarketDataStream[] streams)
    {
        return new MarketDataStreamManager(
            streams,
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            symbolsCache,
            NullLogger<MarketDataStreamManager>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotThrow_WhenStreamStartFails()
    {
        var failingStream = new Mock<IMarketDataStream>();
        failingStream.Setup(s => s.ExchangeName).Returns("Failing");
        failingStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("WS subscription failed"));

        var sut = CreateSut(failingStream.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // ExecuteAsync should complete without throwing — exception is caught internally
        var act = () => sut.StartAsync(cts.Token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesOtherStreams_WhenOneStreamFails()
    {
        var failingStream = new Mock<IMarketDataStream>();
        failingStream.Setup(s => s.ExchangeName).Returns("Failing");
        failingStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var healthyCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var healthyStream = new Mock<IMarketDataStream>();
        healthyStream.Setup(s => s.ExchangeName).Returns("Healthy");
        healthyStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => healthyCalled.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(failingStream.Object, healthyStream.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        await healthyCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // The healthy stream's StartAsync should have been called despite the other failing
        healthyStream.Verify(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CompletesGracefully_OnCancellation()
    {
        var stream = new Mock<IMarketDataStream>();
        stream.Setup(s => s.ExchangeName).Returns("Test");
        stream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(stream.Object);
        using var cts = new CancellationTokenSource();

        // Cancel immediately
        cts.Cancel();

        var act = () => sut.StartAsync(cts.Token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopAsync_DisposesAllStreams()
    {
        var stream1 = new Mock<IMarketDataStream>();
        stream1.Setup(s => s.ExchangeName).Returns("Stream1");
        stream1.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        stream1.Setup(s => s.StopAsync()).Returns(Task.CompletedTask);
        stream1.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var stream2 = new Mock<IMarketDataStream>();
        stream2.Setup(s => s.ExchangeName).Returns("Stream2");
        stream2.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        stream2.Setup(s => s.StopAsync()).Returns(Task.CompletedTask);
        stream2.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var sut = CreateSut(stream1.Object, stream2.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        // Give ExecuteAsync time to start streams before stopping
        await Task.Delay(100);
        await sut.StopAsync(CancellationToken.None);

        stream1.Verify(s => s.StopAsync(), Times.Once);
        stream1.Verify(s => s.DisposeAsync(), Times.Once);
        stream2.Verify(s => s.StopAsync(), Times.Once);
        stream2.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task StopAsync_ContinuesDisposal_WhenOneStreamFails()
    {
        var failingStream = new Mock<IMarketDataStream>();
        failingStream.Setup(s => s.ExchangeName).Returns("Failing");
        failingStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        failingStream.Setup(s => s.StopAsync())
            .ThrowsAsync(new InvalidOperationException("Stop failed"));
        failingStream.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var healthyStream = new Mock<IMarketDataStream>();
        healthyStream.Setup(s => s.ExchangeName).Returns("Healthy");
        healthyStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        healthyStream.Setup(s => s.StopAsync()).Returns(Task.CompletedTask);
        healthyStream.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var sut = CreateSut(failingStream.Object, healthyStream.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        await Task.Delay(100);
        await sut.StopAsync(CancellationToken.None);

        // Even though the first stream failed to stop, the second should still be disposed
        healthyStream.Verify(s => s.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task StopAsync_CompletesGracefully_WhenNoStreamsStarted()
    {
        var stream = new Mock<IMarketDataStream>();
        stream.Setup(s => s.ExchangeName).Returns("Test");
        stream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        stream.Setup(s => s.StopAsync()).Returns(Task.CompletedTask);
        stream.Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var sut = CreateSut(stream.Object);
        using var cts = new CancellationTokenSource();

        // Cancel immediately so ExecuteAsync exits without starting streams
        cts.Cancel();
        await sut.StartAsync(cts.Token);

        // StopAsync should not throw even if streams never started
        var act = () => sut.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── Per-exchange symbol filtering tests ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FiltersSymbolsPerExchange()
    {
        // DB returns BTC, ETH, NVDA
        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(
        [
            new Asset { Id = 1, Symbol = "BTC", IsActive = true },
            new Asset { Id = 2, Symbol = "ETH", IsActive = true },
            new Asset { Id = 3, Symbol = "NVDA", IsActive = true },
        ]);

        // Hyperliquid supports BTC, ETH only (no NVDA)
        var cacheMock = new Mock<IExchangeSupportedSymbolsCache>();
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync("Hyperliquid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["BTC", "ETH"], StringComparer.OrdinalIgnoreCase));
        // Lighter supports BTC, ETH, NVDA (all)
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync("Lighter", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["BTC", "ETH", "NVDA"], StringComparer.OrdinalIgnoreCase));

        List<string>? hlReceivedSymbols = null;
        List<string>? lighterReceivedSymbols = null;

        // N5: deterministic signaling — no Task.Delay
        var hlStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lighterStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var hlStream = new Mock<IMarketDataStream>();
        hlStream.Setup(s => s.ExchangeName).Returns("Hyperliquid");
        hlStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((syms, _) =>
            {
                hlReceivedSymbols = syms.ToList();
                hlStarted.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);

        var lighterStream = new Mock<IMarketDataStream>();
        lighterStream.Setup(s => s.ExchangeName).Returns("Lighter");
        lighterStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((syms, _) =>
            {
                lighterReceivedSymbols = syms.ToList();
                lighterStarted.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);

        var sut = CreateSut(cacheMock.Object, hlStream.Object, lighterStream.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        await Task.WhenAll(
            hlStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            lighterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await sut.StopAsync(CancellationToken.None);

        hlReceivedSymbols.Should().NotBeNull();
        hlReceivedSymbols.Should().BeEquivalentTo(["BTC", "ETH"],
            "NVDA is not supported on Hyperliquid and should be filtered out");

        lighterReceivedSymbols.Should().NotBeNull();
        lighterReceivedSymbols.Should().BeEquivalentTo(["BTC", "ETH", "NVDA"],
            "Lighter supports all three symbols");
    }

    [Fact]
    public async Task ExecuteAsync_CacheEmpty_PassesFullList()
    {
        // Cache returns empty set — graceful degradation means full list passes through
        var cacheMock = new Mock<IExchangeSupportedSymbolsCache>();
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        List<string>? receivedSymbols = null;
        // N5: deterministic signaling — no Task.Delay
        var streamStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Mock<IMarketDataStream>();
        stream.Setup(s => s.ExchangeName).Returns("Hyperliquid");
        stream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((syms, _) =>
            {
                receivedSymbols = syms.ToList();
                streamStarted.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);

        var sut = CreateSut(cacheMock.Object, stream.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        receivedSymbols.Should().NotBeNull();
        receivedSymbols.Should().BeEquivalentTo(["ETH", "BTC"],
            "empty cache means full unfiltered list is passed as fallback");
    }

    [Fact]
    public async Task ExecuteAsync_LogsSkippedSymbols()
    {
        // DB returns BTC, ETH, NVDA
        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(
        [
            new Asset { Id = 1, Symbol = "BTC", IsActive = true },
            new Asset { Id = 2, Symbol = "ETH", IsActive = true },
            new Asset { Id = 3, Symbol = "NVDA", IsActive = true },
        ]);

        // Exchange only supports BTC, ETH
        var cacheMock = new Mock<IExchangeSupportedSymbolsCache>();
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync("TestExchange", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["BTC", "ETH"], StringComparer.OrdinalIgnoreCase));

        // N5: deterministic signaling — no Task.Delay
        var streamStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Mock<IMarketDataStream>();
        stream.Setup(s => s.ExchangeName).Returns("TestExchange");
        stream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => streamStarted.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var mockLogger = new Mock<ILogger<MarketDataStreamManager>>();

        var sut = new MarketDataStreamManager(
            [stream.Object],
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            cacheMock.Object,
            mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.StartAsync(cts.Token);
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Verify that an Information-level log was emitted about skipped symbols
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("symbols supported, skipped")),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once);
    }

    // ── NB6: cache throws for one exchange but other streams still start ─────────

    [Fact]
    public async Task ExecuteAsync_CacheThrows_OtherStreamsStillStart()
    {
        // GetSupportedSymbolsAsync throws for Hyperliquid, succeeds for Lighter
        var cacheMock = new Mock<IExchangeSupportedSymbolsCache>();
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync("Hyperliquid", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cache unavailable for Hyperliquid"));
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync("Lighter", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(ActiveAssets.Select(a => a.Symbol), StringComparer.OrdinalIgnoreCase));

        var lighterStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var hlStream = new Mock<IMarketDataStream>();
        hlStream.Setup(s => s.ExchangeName).Returns("Hyperliquid");
        hlStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var lighterStream = new Mock<IMarketDataStream>();
        lighterStream.Setup(s => s.ExchangeName).Returns("Lighter");
        lighterStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => lighterStarted.TrySetResult(true))
            .Returns(Task.CompletedTask);

        // NB8: use a mock logger to verify the error-log branch fires for Hyperliquid.
        var mockLogger = new Mock<ILogger<MarketDataStreamManager>>();
        var sut = new MarketDataStreamManager(
            [hlStream.Object, lighterStream.Object],
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            cacheMock.Object,
            mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Starting the service must not throw — exception is caught per-stream
        var act = () => sut.StartAsync(cts.Token);
        await act.Should().NotThrowAsync("a cache exception for one exchange must not crash the service");

        // Lighter stream should still have started
        await lighterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lighterStream.Verify(
            s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Lighter StartAsync must be invoked despite Hyperliquid cache throwing");

        // Hyperliquid stream must NOT have been started (cache threw before reaching StartAsync)
        hlStream.Verify(
            s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Hyperliquid StartAsync must not be invoked when its cache lookup throws");

        // NB8: verify LogError was called once with a message referencing Hyperliquid.
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Hyperliquid")),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once,
            "a LogError containing 'Hyperliquid' must be emitted when the cache throws for that exchange");

        await sut.StopAsync(CancellationToken.None);
    }

    // ── NB7: empty intersection — observed behavior locked in ────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyIntersection_BehaviorLockedIn()
    {
        // DB symbols do NOT intersect the cache's supported set.
        // DB: FOO-USD; cache: BAR-USD only.
        // Current production behavior: the intersection is empty, so filtered list is empty;
        // StartAsync IS called with an empty list (the cache is non-empty, so the empty
        // intersection is passed through rather than falling back to the full list).
        // This test locks that contract in so a future change is explicit.
        var cacheMock = new Mock<IExchangeSupportedSymbolsCache>();
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(["BAR-USD"], StringComparer.OrdinalIgnoreCase));

        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(
        [
            new Asset { Id = 1, Symbol = "FOO-USD", IsActive = true },
        ]);

        List<string>? receivedSymbols = null;
        var streamStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new Mock<IMarketDataStream>();
        stream.Setup(s => s.ExchangeName).Returns("Hyperliquid");
        stream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((syms, _) =>
            {
                receivedSymbols = syms.ToList();
                streamStarted.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);

        // NB9: inject a logger mock to verify the skip-log fires with the correct count.
        var mockLogger = new Mock<ILogger<MarketDataStreamManager>>();
        var sut = new MarketDataStreamManager(
            [stream.Object],
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            cacheMock.Object,
            mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Contract: when cache is non-empty but intersection is empty, StartAsync is called
        // with an empty list (not skipped). To change this behavior, update this assertion.
        stream.Verify(
            s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "StartAsync must be invoked even when the intersection is empty");
        receivedSymbols.Should().NotBeNull();
        receivedSymbols.Should().BeEmpty(
            "empty intersection with a non-empty cache produces an empty symbol list — StartAsync still runs");

        // NB9: all DB symbols are skipped (FOO-USD not in cache), so skip-log must fire once
        //      and its message must reference the skipped count (1 == DB symbol count).
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("symbols supported, skipped")),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once,
            "skip-log must fire exactly once when all DB symbols are filtered out");
    }

    // ── NB10: B1 fix regression — two streams with empty cache share no list reference ─

    [Fact]
    public async Task ExecuteAsync_CacheEmpty_TwoStreams_DoNotShareListReference()
    {
        // Empty cache — both streams get the full-list fallback (B1: .ToList() each time).
        var cacheMock = new Mock<IExchangeSupportedSymbolsCache>();
        cacheMock
            .Setup(c => c.GetSupportedSymbolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        List<string>? receivedA = null;
        List<string>? receivedB = null;

        var aStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var streamA = new Mock<IMarketDataStream>();
        streamA.Setup(s => s.ExchangeName).Returns("ExchangeA");
        streamA.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((syms, _) =>
            {
                receivedA = syms.ToList();
                aStarted.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);

        var streamB = new Mock<IMarketDataStream>();
        streamB.Setup(s => s.ExchangeName).Returns("ExchangeB");
        streamB.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((syms, _) =>
            {
                receivedB = syms.ToList();
                bStarted.TrySetResult(true);
            })
            .Returns(Task.CompletedTask);

        var sut = CreateSut(cacheMock.Object, streamA.Object, streamB.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await sut.StartAsync(cts.Token);
        await Task.WhenAll(
            aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await sut.StopAsync(CancellationToken.None);

        receivedA.Should().NotBeNull();
        receivedB.Should().NotBeNull();

        // NB10: B1 fix guarantees each stream received an independent list — not the same reference.
        receivedA.Should().NotBeSameAs(receivedB,
            "each stream must receive an independent list instance so mutating one does not affect the other");

        // Both lists should contain the same symbols (the full active-assets fallback).
        receivedA.Should().BeEquivalentTo(receivedB,
            "both streams get the same symbols, just in independent list instances");
    }

    // ── nit6: no active assets → no streams started ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoActiveAssets_NoStreamsStarted()
    {
        // Override the default active-assets setup to return an empty list.
        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(new List<Asset>());

        var stream = new Mock<IMarketDataStream>();
        stream.Setup(s => s.ExchangeName).Returns("Hyperliquid");

        var mockLogger = new Mock<ILogger<MarketDataStreamManager>>();
        var sut = new MarketDataStreamManager(
            [stream.Object],
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            _mockSymbolsCache.Object,
            mockLogger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.StartAsync(cts.Token);
        // Give ExecuteAsync time to run the early-exit path
        await Task.Delay(200);
        await sut.StopAsync(CancellationToken.None);

        // No stream should have been started
        stream.Verify(
            s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "StartAsync must not be called when there are no active assets");

        // A warning must be logged about no active assets
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("No active assets")),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once,
            "a warning must be logged when no active assets are found");
    }
}
