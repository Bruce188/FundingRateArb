using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class AlertRepository : IAlertRepository
{
    private readonly AppDbContext _context;

    public AlertRepository(AppDbContext context) => _context = context;

    public Task<List<Alert>> GetAllAsync(bool unreadOnly = false) =>
        _context.Alerts
            .Where(a => !unreadOnly || !a.IsRead)
            .OrderByDescending(a => a.CreatedAt)
            .Take(500)
            .ToListAsync();

    public Task<Alert?> GetByIdAsync(int id) =>
        _context.Alerts.FirstOrDefaultAsync(a => a.Id == id);

    public Task<List<Alert>> GetByUserAsync(string userId, bool unreadOnly = false, int skip = 0, int take = 500) =>
        _context.Alerts
            .Where(a => a.UserId == userId && (!unreadOnly || !a.IsRead))
            .OrderByDescending(a => a.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

    public Task<List<Alert>> GetByPositionAsync(int positionId) =>
        _context.Alerts
            .Where(a => a.ArbitragePositionId == positionId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .ToListAsync();

    public Task<Alert?> GetRecentAsync(string userId, int? positionId, AlertType type, TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        return _context.Alerts
            .Where(a => a.UserId == userId
                        && a.ArbitragePositionId == positionId
                        && a.Type == type
                        && a.CreatedAt >= cutoff)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<Dictionary<(int PositionId, AlertType Type), Alert>> GetRecentByPositionIdsAsync(
        IEnumerable<int> positionIds, IEnumerable<AlertType> types, TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        var posIds = positionIds.ToList();
        var typeList = types.ToList();

        var alerts = await _context.Alerts
            .Where(a => a.ArbitragePositionId.HasValue
                        && posIds.Contains(a.ArbitragePositionId.Value)
                        && typeList.Contains(a.Type)
                        && a.CreatedAt >= cutoff)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        // Keep only the most recent alert per (positionId, type)
        return alerts
            .GroupBy(a => (a.ArbitragePositionId!.Value, a.Type))
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<List<Alert>> GetRecentUnreadAsync(TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        return await _context.Alerts
            .Where(a => !a.IsRead && a.CreatedAt >= cutoff)
            .OrderByDescending(a => a.CreatedAt)
            .Take(100)
            .ToListAsync();
    }

    public async Task<Dictionary<AlertSeverity, int>> GetSeverityCountsAsync(TimeSpan within)
    {
        var cutoff = DateTime.UtcNow - within;
        var counts = await _context.Alerts
            .Where(a => a.CreatedAt >= cutoff)
            .GroupBy(a => a.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync();

        return counts.ToDictionary(x => x.Severity, x => x.Count);
    }

    public void Add(Alert alert) => _context.Alerts.Add(alert);

    public void Update(Alert alert) => _context.Alerts.Update(alert);

    public Task MarkAllReadAsync(string userId) =>
        _context.Alerts
            .Where(a => a.UserId == userId && !a.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true));
}
