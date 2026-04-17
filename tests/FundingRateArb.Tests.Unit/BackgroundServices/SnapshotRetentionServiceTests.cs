using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
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
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
        var spaceProbe = new Mock<IDatabaseSpaceHealthProbe>();
        spaceProbe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);
        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object, spaceProbe.Object);

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
                It.IsAny<bool>(),
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
                It.IsAny<bool>(),
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
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task LogsPurgeResult()
    {
        var repository = new Mock<IFundingRateRepository>();
        repository
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(500);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();
        var spaceProbe = new Mock<IDatabaseSpaceHealthProbe>();
        spaceProbe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object, spaceProbe.Object);

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
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();
        var spaceProbe = new Mock<IDatabaseSpaceHealthProbe>();
        spaceProbe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object, spaceProbe.Object);

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

    // ── New observability + self-recovery tests ──────────────────────────────

    private static (SnapshotRetentionService Service, Mock<IFundingRateRepository> Repo, Mock<ILogger<SnapshotRetentionService>> Logger, Mock<IDatabaseSpaceHealthProbe> Probe)
        BuildSutFull(double probeRatio = 0.0, bool probeThrows = false)
    {
        var repo = new Mock<IFundingRateRepository>();
        repo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();

        var probe = new Mock<IDatabaseSpaceHealthProbe>();
        if (probeThrows)
        {
            probe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("probe error"));
        }
        else
        {
            probe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(probeRatio);
        }

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object, probe.Object);
        return (service, repo, logger, probe);
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_EmitsInformationStartLog_OnEveryInvocation()
    {
        var (service, _, logger, _) = BuildSutFull();

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("cycle starting")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2),
            "start-log must be emitted once per invocation");
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_OperationCanceledException_LoggedAtDebug_AndRethrown()
    {
        var repo = new Mock<IFundingRateRepository>();
        repo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("canceled"));

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();
        var probe = new Mock<IDatabaseSpaceHealthProbe>();
        probe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object, probe.Object);

        // Assert propagated
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.PurgeExpiredSnapshotsAsync(CancellationToken.None));

        // Assert debug log
        logger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("canceled")),
                It.IsAny<OperationCanceledException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "OCE must be logged at Debug level");
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_WithinThrottleWindow_DoesNotInvokeForce()
    {
        // probe > 80% but second call is within 24h window
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.90);

        // First call: sets _lastForceOverrideUtc (force=true)
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        // Second call: within 24h window → force must be false
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        // Verify first call passed force=true, second call passed force=false
        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "force=true must be sent only once (first call)");
        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "force=false must be sent on the second call within the throttle window");
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_AfterThrottleWindow_InvokesForceWhenSpaceHigh()
    {
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.90);

        // Simulate first call was 25 hours ago by reflection
        var field = typeof(SnapshotRetentionService)
            .GetField("_lastForceOverrideUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(service, DateTimeOffset.UtcNow.AddHours(-25));

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "force=true must be passed after the 24h throttle window has elapsed");
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_ProbeThrows_DefaultsForceFalse_AndLogsWarning()
    {
        var (service, repo, logger, _) = BuildSutFull(probeThrows: true);

        // Should not throw
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        // force must default to false when probe throws
        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "force must default to false when probe throws");

        // Warning logged
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, _) => o.ToString()!.Contains("probe failed") || o.ToString()!.Contains("Space probe")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "a warning must be logged when the space probe throws");
    }
}
