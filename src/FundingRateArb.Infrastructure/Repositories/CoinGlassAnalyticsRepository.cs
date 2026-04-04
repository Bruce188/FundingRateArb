using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class CoinGlassAnalyticsRepository : ICoinGlassAnalyticsRepository
{
    private readonly AppDbContext _context;

    public CoinGlassAnalyticsRepository(AppDbContext context) => _context = context;

    public async Task SaveSnapshotAsync(List<CoinGlassExchangeRate> rates, CancellationToken ct = default)
    {
        if (rates.Count == 0) return;

        // Truncate to hour for dedup
        var snapshotHour = new DateTime(
            rates[0].SnapshotTime.Year,
            rates[0].SnapshotTime.Month,
            rates[0].SnapshotTime.Day,
            rates[0].SnapshotTime.Hour, 0, 0, DateTimeKind.Utc);

        var hourStart = snapshotHour;
        var hourEnd = snapshotHour.AddHours(1);

        // Check for existing snapshot in this hour
        var existingCount = await _context.CoinGlassExchangeRates
            .CountAsync(r => r.SnapshotTime >= hourStart && r.SnapshotTime < hourEnd, ct);

        if (existingCount > 0)
        {
            // Already have data for this hour — skip
            return;
        }

        await _context.CoinGlassExchangeRates.AddRangeAsync(rates, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<List<CoinGlassExchangeRate>> GetLatestSnapshotPerExchangeAsync(CancellationToken ct = default)
    {
        // Get the most recent snapshot time
        var latestTime = await _context.CoinGlassExchangeRates
            .MaxAsync(r => (DateTime?)r.SnapshotTime, ct);

        if (latestTime is null)
            return [];

        // Get all rates from that snapshot hour
        var hourStart = new DateTime(
            latestTime.Value.Year, latestTime.Value.Month, latestTime.Value.Day,
            latestTime.Value.Hour, 0, 0, DateTimeKind.Utc);
        var hourEnd = hourStart.AddHours(1);

        return await _context.CoinGlassExchangeRates
            .Where(r => r.SnapshotTime >= hourStart && r.SnapshotTime < hourEnd)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<List<CoinGlassExchangeRate>> GetSnapshotsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await _context.CoinGlassExchangeRates
            .Where(r => r.SnapshotTime >= from && r.SnapshotTime <= to)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<List<CoinGlassDiscoveryEvent>> GetDiscoveryEventsAsync(int days = 7, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        return await _context.CoinGlassDiscoveryEvents
            .Where(e => e.DiscoveredAt >= cutoff)
            .OrderByDescending(e => e.DiscoveredAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task SaveDiscoveryEventsAsync(List<CoinGlassDiscoveryEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;

        await _context.CoinGlassDiscoveryEvents.AddRangeAsync(events, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task PruneOldSnapshotsAsync(int retentionDays = 7, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        await _context.CoinGlassExchangeRates
            .Where(r => r.SnapshotTime < cutoff)
            .ExecuteDeleteAsync(ct);

        await _context.CoinGlassDiscoveryEvents
            .Where(e => e.DiscoveredAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<HashSet<(string Exchange, string Symbol)>> GetKnownPairsAsync(CancellationToken ct = default)
    {
        var pairs = await _context.CoinGlassExchangeRates
            .Select(r => new { r.SourceExchange, r.Symbol })
            .Distinct()
            .AsNoTracking()
            .ToListAsync(ct);

        return pairs.Select(p => (p.SourceExchange, p.Symbol)).ToHashSet();
    }
}
