using FluentAssertions;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.HealthChecks;

public class DatabaseHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenQuerySucceeds()
    {
        var sut = new StubDatabaseHealthCheck(probe: _ => Task.CompletedTask);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("database reachable");
    }

    [Fact]
    public async Task CheckHealthAsync_Degraded_WhenQueryTimesOut()
    {
        // Probe blocks for 5 seconds — the health check must fire its 2s timeout.
        var sut = new StubDatabaseHealthCheck(
            probe: async ct => await Task.Delay(TimeSpan.FromSeconds(5), ct));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("timed out");
    }

    [Fact]
    public async Task CheckHealthAsync_Degraded_WhenLoginFails()
    {
        // Simulate a login-phase SqlException by throwing a regular exception —
        // the health check must not care about the exception type; it must return
        // Degraded for any non-cancellation failure.
        var loginEx = new InvalidOperationException(
            "A network-related or instance-specific error occurred (error 35).");
        var sut = new StubDatabaseHealthCheck(
            probe: _ => throw loginEx);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("transient failure");
        // review-v131 B2: the raw exception must NOT be attached to the HealthCheckResult
        // because the default /healthz response writer can serialize Exception.Message
        // into the public response body. SqlException messages on login-phase failures
        // commonly contain the server name, database name, and username — we must not
        // leak infrastructure topology to anonymous callers.
        result.Exception.Should().BeNull("public /healthz response must not leak exception detail");
    }

    [Fact]
    public async Task CheckHealthAsync_Degraded_NotUnhealthy_OnTransientFailure()
    {
        // Regression: no code path should return Unhealthy. Azure restarts the
        // container on Unhealthy, which amplifies production outages during SQL
        // blips.
        var sut = new StubDatabaseHealthCheck(
            probe: _ => throw new TimeoutException("simulated"));

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().NotBe(HealthStatus.Unhealthy);
        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealthAsync_PropagatesCancellation_WhenHostShutsDown()
    {
        // If the caller cancels (host shutdown), we must not swallow that as a
        // Degraded result — the runtime expects the exception to propagate.
        var probeStarted = new TaskCompletionSource();
        var sut = new StubDatabaseHealthCheck(
            probe: async ct =>
            {
                probeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });

        using var cts = new CancellationTokenSource();
        var task = sut.CheckHealthAsync(new HealthCheckContext(), cts.Token);
        await probeStarted.Task;
        cts.Cancel();

        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_AcceptsRetryLessOptions_WithoutEnableRetryOnFailure()
    {
        // Arrange: build DbContextOptions without EnableRetryOnFailure
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=localhost;Database=FakeDb;Trusted_Connection=True;")
            .Options;

        // Act: constructing with a factory backed by retry-less options must succeed.
        // We use a PooledDbContextFactory so we exercise the real constructor path —
        // no SQL connection is opened at construction time.
        var factory = new PooledDbContextFactory<AppDbContext>(options);
        var act = () => new DatabaseHealthCheck(factory, NullLogger<DatabaseHealthCheck>.Instance);
        act.Should().NotThrow();

        // Assert: the options must not have EnableRetryOnFailure configured.
        // RelationalOptionsExtension.ExecutionStrategyFactory is only non-null when
        // EnableRetryOnFailure (or a custom execution strategy) is set — null means
        // the default non-retrying strategy is used.
        var relationalExt = RelationalOptionsExtension.Extract(options);
        relationalExt.ExecutionStrategyFactory.Should().BeNull(
            "health-check factory must not have EnableRetryOnFailure");
    }

    // ── Stub that overrides ProbeAsync for testability ───────────────────────────

    private sealed class StubDatabaseHealthCheck : DatabaseHealthCheck
    {
        private readonly Func<CancellationToken, Task> _probe;

        public StubDatabaseHealthCheck(Func<CancellationToken, Task> probe)
            : base(
                new Mock<IDbContextFactory<AppDbContext>>().Object,
                NullLogger<DatabaseHealthCheck>.Instance)
        {
            _probe = probe;
        }

        protected override Task ProbeAsync(CancellationToken ct) => _probe(ct);
    }
}
