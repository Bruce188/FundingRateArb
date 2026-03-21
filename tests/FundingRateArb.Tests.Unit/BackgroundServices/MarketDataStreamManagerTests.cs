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

        var healthyStream = new Mock<IMarketDataStream>();
        healthyStream.Setup(s => s.ExchangeName).Returns("Healthy");
        healthyStream.Setup(s => s.StartAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut(failingStream.Object, healthyStream.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await sut.StartAsync(cts.Token);
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
}
