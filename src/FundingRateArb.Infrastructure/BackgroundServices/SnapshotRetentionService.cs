using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

/// <summary>
/// Purges FundingRateSnapshots older than the configured retention period.
/// Runs every 6 hours aligned to 00:00/06:00/12:00/18:00 UTC, with an
/// initial purge 30 seconds after startup.
/// </summary>
public class SnapshotRetentionService : BackgroundService
{
    private const int DefaultRetentionDays = 14;
    private const int CadenceHours = 6;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SnapshotRetentionService> _logger;

    private DateTimeOffset _lastForceOverrideUtc = DateTimeOffset.MinValue;

    public SnapshotRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SnapshotRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial startup delay to let migrations complete
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Initial purge on startup
        await PurgeExpiredSnapshotsAsync(ct);

        // Enter 6-hour aligned loop
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextRun = ComputeNextAlignedRun(now);
            var delay = nextRun - now;

            _logger.LogDebug("SnapshotRetentionService next run at {NextRun} (in {Delay})", nextRun, delay);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await PurgeExpiredSnapshotsAsync(ct);
        }
    }

    internal async Task PurgeExpiredSnapshotsAsync(CancellationToken ct)
    {
        var retentionDays = GetRetentionDays();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        _logger.LogInformation(
            "SnapshotRetentionService cycle starting at {Timestamp}, cutoff {CutoffDate}",
            DateTimeOffset.UtcNow, cutoff);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IFundingRateRepository>();
            // Singleton BackgroundService must not capture Scoped services directly —
            // resolve IDatabaseSpaceHealthProbe per iteration via the scope to honour its Scoped lifetime.
            var probe = scope.ServiceProvider.GetRequiredService<IDatabaseSpaceHealthProbe>();

            // Force-override: at most once per 24 h when storage > 80%
            var force = false;
            if (DateTimeOffset.UtcNow - _lastForceOverrideUtc >= TimeSpan.FromHours(24))
            {
                // Skip the probe entirely when there are no suppressed purges to recover.
                if (repo.GetSuppressedPurgeCount() > 0)
                {
                    try
                    {
                        var ratio = await probe.GetUsedSpaceRatioAsync(ct);
                        if (ratio > 0.80)
                        {
                            force = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Space probe failed; skipping force override.");
                    }
                }
            }

            int purgedCount;
            if (force)
            {
                purgedCount = await repo.PurgeOlderThanAsync(cutoff, true, ct);
                // Update the throttle clock only after a successful forced purge —
                // an exception on the forced path must leave the clock untouched so
                // the next cadence opportunity is not blocked.
                _lastForceOverrideUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                purgedCount = await repo.PurgeOlderThanAsync(cutoff, false, ct);

                // Emit audit log when an aligned-cadence purge is suppressed by the cooldown.
                var suppressedCount = repo.GetSuppressedPurgeCount();
                if (suppressedCount > 0 && purgedCount == 0)
                {
                    var nextAttempt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
                    _logger.LogInformation(
                        "Snapshot retention purge suppressed: {SuppressedPurgeCount} pending, next attempt at {NextAttempt}",
                        suppressedCount, nextAttempt);
                }
            }

            _logger.LogInformation(
                "SnapshotRetentionService purged {Count} snapshots older than {CutoffDate}",
                purgedCount,
                cutoff);
        }
        catch (OperationCanceledException oce)
        {
            _logger.LogDebug(oce, "SnapshotRetentionService cycle canceled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SnapshotRetentionService purge failed");
        }
    }

    private int GetRetentionDays()
    {
        var configValue = _configuration["DataRetention:FundingRateSnapshotDays"];

        if (int.TryParse(configValue, out var days) && days > 0)
        {
            return days;
        }

        return DefaultRetentionDays;
    }

    private static DateTime ComputeNextAlignedRun(DateTime now)
    {
        // Align to 00:00/06:00/12:00/18:00 UTC
        var currentSlot = now.Hour / CadenceHours;
        var nextSlotHour = (currentSlot + 1) * CadenceHours;

        if (nextSlotHour >= 24)
        {
            return now.Date.AddDays(1);
        }

        return now.Date.AddHours(nextSlotHour);
    }
}
