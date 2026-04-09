using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.HealthChecks;

/// <summary>
/// Executes <c>SELECT 1</c> against the application database with a 2-second timeout.
/// Any failure (timeout, login error, transient SQL exception) is reported as
/// <see cref="HealthStatus.Degraded"/> rather than <see cref="HealthStatus.Unhealthy"/>
/// so Azure does not restart the container during brief connectivity blips.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(
        IDbContextFactory<AppDbContext> factory,
        ILogger<DatabaseHealthCheck> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(QueryTimeout);

        try
        {
            await ProbeAsync(cts.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("database reachable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Database health check timed out after {TimeoutSeconds}s",
                QueryTimeout.TotalSeconds);
            return HealthCheckResult.Degraded(
                $"database query timed out (>{QueryTimeout.TotalSeconds:0}s)");
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation (host shutdown) — propagate to the runtime.
            throw;
        }
        catch (Exception ex)
        {
            // B2 from review-v131: never pass the raw exception into the HealthCheckResult
            // that ASP.NET Core serializes out to the public /healthz endpoint — SqlException
            // messages on login-phase failures commonly contain the server name, database name,
            // and username. Log the full exception server-side only.
            _logger.LogWarning(ex, "Database health check failed with transient error");
            return HealthCheckResult.Degraded("database transient failure");
        }
    }

    /// <summary>
    /// Runs the actual probe. Virtual so tests can override without needing a real
    /// SQL Server connection. On relational providers (production SQL Server) we
    /// execute <c>SELECT 1</c> which exercises the full login + query path. On
    /// non-relational providers (e.g. the in-memory EF provider used by integration
    /// tests) we fall back to <see cref="DatabaseFacade.CanConnectAsync"/>, which is
    /// a no-op that always reports true for the in-memory store.
    /// </summary>
    protected virtual async Task ProbeAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (db.Database.IsRelational())
        {
            _ = await db.Database.ExecuteSqlRawAsync("SELECT 1", ct).ConfigureAwait(false);
        }
        else
        {
            _ = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
        }
    }
}
