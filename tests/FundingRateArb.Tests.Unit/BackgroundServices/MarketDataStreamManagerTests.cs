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
    }

    private MarketDataStreamManager CreateSut(params IMarketDataStream[] streams)
    {
        return new MarketDataStreamManager(
            streams,
            _mockScopeFactory.Object,
            _mockHubContext.Object,
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
}
