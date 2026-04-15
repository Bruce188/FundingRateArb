using FundingRateArb.Application.Common.Repositories;
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
        try
        {
            var retentionDays = GetRetentionDays();
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IFundingRateRepository>();

            var purgedCount = await repository.PurgeOlderThanAsync(cutoff, ct);

            _logger.LogInformation(
                "SnapshotRetentionService purged {Count} snapshots older than {CutoffDate}",
                purgedCount,
                cutoff);
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
