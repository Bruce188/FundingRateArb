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
}
