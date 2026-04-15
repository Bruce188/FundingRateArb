using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class SnapshotRetentionServiceTests
{
    private static (SnapshotRetentionService Service, Mock<IFundingRateRepository> Repository)
        BuildSut(Dictionary<string, string?>? configValues = null)
    {
        var repository = new Mock<IFundingRateRepository>();
        repository
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configBuilder = new ConfigurationBuilder();
        if (configValues is not null)
        {
            configBuilder.AddInMemoryCollection(configValues);
        }
        var configuration = configBuilder.Build();

        var logger = new Mock<ILogger<SnapshotRetentionService>>();
        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);

        return (service, repository);
    }

    [Fact]
    public async Task PurgesSnapshotsOlderThanConfiguredDays()
    {
        var config = new Dictionary<string, string?>
        {
            ["DataRetention:FundingRateSnapshotDays"] = "14"
        };
        var (service, repository) = BuildSut(config);

        var before = DateTime.UtcNow;
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);
        var after = DateTime.UtcNow;

        repository.Verify(
            r => r.PurgeOlderThanAsync(
                It.Is<DateTime>(d =>
                    d >= before.AddDays(-14).AddMinutes(-1) &&
                    d <= after.AddDays(-14).AddMinutes(1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UsesCustomRetentionDaysFromConfig()
    {
        var config = new Dictionary<string, string?>
        {
            ["DataRetention:FundingRateSnapshotDays"] = "7"
        };
        var (service, repository) = BuildSut(config);

        var before = DateTime.UtcNow;
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);
        var after = DateTime.UtcNow;

        repository.Verify(
            r => r.PurgeOlderThanAsync(
                It.Is<DateTime>(d =>
                    d >= before.AddDays(-7).AddMinutes(-1) &&
                    d <= after.AddDays(-7).AddMinutes(1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DefaultsTo14DaysWhenConfigMissing()
    {
        // No config values at all
        var (service, repository) = BuildSut();

        var before = DateTime.UtcNow;
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);
        var after = DateTime.UtcNow;

        repository.Verify(
            r => r.PurgeOlderThanAsync(
                It.Is<DateTime>(d =>
                    d >= before.AddDays(-14).AddMinutes(-1) &&
                    d <= after.AddDays(-14).AddMinutes(1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LogsPurgeResult()
    {
        var repository = new Mock<IFundingRateRepository>();
        repository
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(500);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("500")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task HandlesExceptionWithoutCrashing()
    {
        var repository = new Mock<IFundingRateRepository>();
        repository
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);

        // Should not throw — the method catches and logs internally
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("purge failed")),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
