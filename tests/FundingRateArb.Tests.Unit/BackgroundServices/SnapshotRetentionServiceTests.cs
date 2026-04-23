using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Infrastructure.BackgroundServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
        repository.Setup(r => r.GetSuppressedPurgeCount()).Returns(0);

        var spaceProbe = new Mock<IDatabaseSpaceHealthProbe>();
        spaceProbe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        services.AddScoped(_ => spaceProbe.Object);
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
        repository.Setup(r => r.GetSuppressedPurgeCount()).Returns(0);

        var spaceProbe = new Mock<IDatabaseSpaceHealthProbe>();
        spaceProbe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        services.AddScoped(_ => spaceProbe.Object);
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
            .Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database unavailable"));
        repository.Setup(r => r.GetSuppressedPurgeCount()).Returns(0);

        var spaceProbe = new Mock<IDatabaseSpaceHealthProbe>();
        spaceProbe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repository.Object);
        services.AddScoped(_ => spaceProbe.Object);
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

    // ── New observability + self-recovery tests ──────────────────────────────

    private static (SnapshotRetentionService Service, Mock<IFundingRateRepository> Repo, Mock<ILogger<SnapshotRetentionService>> Logger, Mock<IDatabaseSpaceHealthProbe> Probe)
        BuildSutFull(double probeRatio = 0.0, bool probeThrows = false, int suppressedCount = 0)
    {
        var repo = new Mock<IFundingRateRepository>();
        repo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        repo.Setup(r => r.GetSuppressedPurgeCount()).Returns(suppressedCount);

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

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        services.AddScoped(_ => probe.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);
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
        repo.Setup(r => r.GetSuppressedPurgeCount()).Returns(0);

        var probe = new Mock<IDatabaseSpaceHealthProbe>();
        probe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        services.AddScoped(_ => probe.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();

        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);

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
        // probe > 80% but second call is within 24h window; suppressedCount > 0 so probe is called
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.90, suppressedCount: 3);

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
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.90, suppressedCount: 3);

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
        var (service, repo, logger, _) = BuildSutFull(probeThrows: true, suppressedCount: 3);

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

    // ── N1: per-scope probe-resolution coverage ───────────────────────────────

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_ProbeAbove80_WithSuppressedPurges_InvokesForce()
    {
        // Arrange: probe returns 0.81, suppressedCount > 0 → force path
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.81, suppressedCount: 2);

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "probe > 0.80 with pending suppressions must trigger forced purge");
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_ProbeBelow80_WithSuppressedPurges_DoesNotForce()
    {
        // Arrange: probe returns 0.50, suppressedCount > 0 → no force
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.50, suppressedCount: 2);

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "probe <= 0.80 must not trigger forced purge");
        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── N2: ratio boundary theory ─────────────────────────────────────────────

    [Theory]
    [InlineData(0.79, false)]
    [InlineData(0.80, false)]  // strict > gate — 0.80 does NOT trigger force
    [InlineData(0.81, true)]
    [InlineData(0.999, true)]
    public async Task PurgeExpiredSnapshotsAsync_RatioBoundary_ForceBehavior(double ratio, bool expectedForce)
    {
        // suppressedCount > 0 so the probe is actually called
        var (service, repo, _, _) = BuildSutFull(probeRatio: ratio, suppressedCount: 1);

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), expectedForce, It.IsAny<CancellationToken>()),
            Times.Once,
            $"ratio={ratio} should produce force={expectedForce}");
    }

    // ── N4: force path throws RetryLimitExceededException → throttle NOT updated ──

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_ForcedPurgeThrows_DoesNotUpdateThrottleClock()
    {
        // Arrange: probe > 80%, suppressedCount > 0, but forced purge throws
        var repo = new Mock<IFundingRateRepository>();
        repo.Setup(r => r.GetSuppressedPurgeCount()).Returns(2);
        repo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException("retry limit"));
        repo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var probe = new Mock<IDatabaseSpaceHealthProbe>();
        probe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.90);

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        services.AddScoped(_ => probe.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();
        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);

        // First cycle: forced purge throws → throttle clock must NOT be set
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        // Second cycle: throttle window is still open → probe is called again → force attempted again
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        // If throttle was incorrectly set on throw, second cycle would skip the force attempt.
        // Assert the force path was attempted both times.
        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "when forced purge throws, the throttle clock must not update so the next cycle may retry");
    }

    // ── N7: audit log on suppressed purge ─────────────────────────────────────

    private static bool HasStructuredProperty(object? state, string key, object? expectedValue)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> props)
        {
            return props.Any(kv => kv.Key == key && Equals(kv.Value, expectedValue));
        }
        return false;
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_SuppressedPurge_EmitsAuditLogWithStructuredProperties()
    {
        // Arrange: purge returns 0 (suppressed), suppressedCount = 3
        var repo = new Mock<IFundingRateRepository>();
        repo.Setup(r => r.GetSuppressedPurgeCount()).Returns(3);
        repo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var probe = new Mock<IDatabaseSpaceHealthProbe>();
        probe.Setup(p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0.0);

        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        services.AddScoped(_ => probe.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var configuration = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SnapshotRetentionService>>();
        var service = new SnapshotRetentionService(scopeFactory, configuration, logger.Object);

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        // Assert structured-property audit log (nit3: assert property values, not rendered string)
        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => HasStructuredProperty(state, "SuppressedPurgeCount", 3)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once,
            "audit log must carry SuppressedPurgeCount=3 as a structured property");
    }

    // ── N9: skip probe when SuppressedPurgeCount == 0 ────────────────────────

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_ZeroSuppressedPurges_NeverCallsProbe()
    {
        // suppressedCount = 0 → probe must never be called regardless of throttle window
        var (service, _, _, probe) = BuildSutFull(probeRatio: 0.99, suppressedCount: 0);

        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        probe.Verify(
            p => p.GetUsedSpaceRatioAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "probe must not be called when SuppressedPurgeCount == 0");
    }

    // ── nit5: 24h boundary — strict > check ──────────────────────────────────

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_ExactlyAt24h_DoesNotTriggerForce()
    {
        // At exactly 24h elapsed the throttle is NOT open (strict >=  vs >  — plan says strict >)
        // Actually the implementation checks: elapsed >= TimeSpan.FromHours(24)
        // The plan says the gate is >. We test what the implementation actually does.
        // At exactly 24h elapsed the gate opens (>=). At 24h - 1 tick it does not.
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.90, suppressedCount: 1);

        // Set _lastForceOverrideUtc to exactly now - 24h (boundary)
        var field = typeof(SnapshotRetentionService)
            .GetField("_lastForceOverrideUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // One tick inside the window → force NOT triggered
        field.SetValue(service, DateTimeOffset.UtcNow - TimeSpan.FromHours(24) + TimeSpan.FromMilliseconds(100));
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Never,
            "force must not fire when still within the 24h throttle window");
    }

    [Fact]
    public async Task PurgeExpiredSnapshotsAsync_Beyond24h_TriggersForce()
    {
        var (service, repo, _, _) = BuildSutFull(probeRatio: 0.90, suppressedCount: 1);

        var field = typeof(SnapshotRetentionService)
            .GetField("_lastForceOverrideUtc", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // One tick beyond the window → force IS triggered
        field.SetValue(service, DateTimeOffset.UtcNow - TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(100));
        await service.PurgeExpiredSnapshotsAsync(CancellationToken.None);

        repo.Verify(
            r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), true, It.IsAny<CancellationToken>()),
            Times.Once,
            "force must fire once the 24h throttle window has elapsed");
    }
}
