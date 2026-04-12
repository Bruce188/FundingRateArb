using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class BalanceRefreshServiceTests
{
    [Fact]
    public async Task ExecuteAsync_OneCycle_PushesBalanceViaNotifier()
    {
        // Arrange
        var mockAggregator = new Mock<IBalanceAggregator>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockUserConfigRepo = new Mock<IUserConfigurationRepository>();

        var snapshot = new BalanceSnapshotDto();
        mockUserConfigRepo.Setup(r => r.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { "user-1" });
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigRepo.Object);
        mockAggregator.Setup(a => a.GetBalanceSnapshotAsync("user-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => mockUow.Object);
        serviceCollection.AddScoped<IBalanceAggregator>(_ => mockAggregator.Object);
        serviceCollection.AddScoped<ISignalRNotifier>(_ => mockNotifier.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var sut = new BalanceRefreshService(scopeFactory, NullLogger<BalanceRefreshService>.Instance);

        // Act
        await sut.RefreshBalancesAsync(CancellationToken.None);

        // Assert
        mockAggregator.Verify(a => a.GetBalanceSnapshotAsync("user-1", It.IsAny<CancellationToken>()), Times.Once);
        mockNotifier.Verify(n => n.PushBalanceUpdateAsync("user-1", snapshot), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoUsers_DoesNotPush()
    {
        // Arrange
        var mockUow = new Mock<IUnitOfWork>();
        var mockUserConfigRepo = new Mock<IUserConfigurationRepository>();
        mockUserConfigRepo.Setup(r => r.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string>());
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigRepo.Object);

        var mockNotifier = new Mock<ISignalRNotifier>();

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => mockUow.Object);
        serviceCollection.AddScoped<IBalanceAggregator>(_ => Mock.Of<IBalanceAggregator>());
        serviceCollection.AddScoped<ISignalRNotifier>(_ => mockNotifier.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var sut = new BalanceRefreshService(scopeFactory, NullLogger<BalanceRefreshService>.Instance);

        // Act
        await sut.RefreshBalancesAsync(CancellationToken.None);

        // Assert
        mockNotifier.Verify(n => n.PushBalanceUpdateAsync(It.IsAny<string>(), It.IsAny<BalanceSnapshotDto>()), Times.Never);
    }

    [Fact]
    public async Task RefreshBalancesAsync_ContinuesOnPerUserFailure()
    {
        // Arrange: two users, aggregator throws for first user
        var mockAggregator = new Mock<IBalanceAggregator>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockUserConfigRepo = new Mock<IUserConfigurationRepository>();

        var snapshot2 = new BalanceSnapshotDto();
        mockUserConfigRepo.Setup(r => r.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { "user-1", "user-2" });
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigRepo.Object);

        mockAggregator.Setup(a => a.GetBalanceSnapshotAsync("user-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Exchange API unreachable"));
        mockAggregator.Setup(a => a.GetBalanceSnapshotAsync("user-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot2);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => mockUow.Object);
        serviceCollection.AddScoped<IBalanceAggregator>(_ => mockAggregator.Object);
        serviceCollection.AddScoped<ISignalRNotifier>(_ => mockNotifier.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var sut = new BalanceRefreshService(scopeFactory, NullLogger<BalanceRefreshService>.Instance);

        // Act
        await sut.RefreshBalancesAsync(CancellationToken.None);

        // Assert: user-1 failure should not prevent user-2 from being pushed
        mockNotifier.Verify(n => n.PushBalanceUpdateAsync("user-1", It.IsAny<BalanceSnapshotDto>()), Times.Never);
        mockNotifier.Verify(n => n.PushBalanceUpdateAsync("user-2", snapshot2), Times.Once);
    }

    [Fact]
    public async Task RefreshBalancesAsync_CreatesPerUserScope()
    {
        // Arrange: 3 users — verify CreateScope called once for user-id lookup + once per user
        var mockAggregator = new Mock<IBalanceAggregator>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockUserConfigRepo = new Mock<IUserConfigurationRepository>();

        var userIds = new List<string> { "user-1", "user-2", "user-3" };
        mockUserConfigRepo.Setup(r => r.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(userIds);
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigRepo.Object);

        mockAggregator.Setup(a => a.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto());

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => mockUow.Object);
        serviceCollection.AddScoped<IBalanceAggregator>(_ => mockAggregator.Object);
        serviceCollection.AddScoped<ISignalRNotifier>(_ => mockNotifier.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var realScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // Wrap the real scope factory to count calls
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(() => realScopeFactory.CreateScope());

        var sut = new BalanceRefreshService(mockScopeFactory.Object, NullLogger<BalanceRefreshService>.Instance);

        // Act
        await sut.RefreshBalancesAsync(CancellationToken.None);

        // Assert: 1 scope for user-id lookup + 3 scopes for 3 users = 4 total
        mockScopeFactory.Verify(f => f.CreateScope(), Times.Exactly(4));
    }
}
