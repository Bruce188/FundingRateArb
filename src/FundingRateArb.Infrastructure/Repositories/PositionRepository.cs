using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class PositionRepository : IPositionRepository
{
    private readonly AppDbContext _context;

    public PositionRepository(AppDbContext context) => _context = context;

    public Task<ArbitragePosition?> GetByIdAsync(int id) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .FirstOrDefaultAsync(p => p.Id == id);

    public Task<List<ArbitragePosition>> GetOpenAsync() =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .Where(p => p.Status == PositionStatus.Open)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetOpenTrackedAsync() =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => p.Status == PositionStatus.Open)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetByUserAsync(string userId, int skip = 0, int take = 500) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.OpenedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetAllAsync(int skip = 0, int take = 500) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .AsNoTracking()
            .OrderByDescending(p => p.OpenedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

    public Task<List<ArbitragePosition>> GetByStatusAsync(PositionStatus status) =>
        _context.ArbitragePositions
            .Include(p => p.Asset)
            .Include(p => p.LongExchange)
            .Include(p => p.ShortExchange)
            .Where(p => p.Status == status)
            .ToListAsync();

    public void Add(ArbitragePosition position) =>
        _context.ArbitragePositions.Add(position);

    public void Update(ArbitragePosition position) =>
        _context.ArbitragePositions.Update(position);
}
