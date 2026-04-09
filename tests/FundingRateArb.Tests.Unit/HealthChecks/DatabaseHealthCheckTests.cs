using FluentAssertions;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.HealthChecks;
using Microsoft.EntityFrameworkCore;
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
        result.Exception.Should().BeSameAs(loginEx);
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
