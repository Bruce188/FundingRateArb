using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class AssetRepository : IAssetRepository
{
    private readonly AppDbContext _context;

    public AssetRepository(AppDbContext context) => _context = context;

    public Task<List<Asset>> GetAllAsync() =>
        _context.Assets.AsNoTracking().ToListAsync();

    public Task<List<Asset>> GetActiveAsync() =>
        _context.Assets.AsNoTracking().Where(a => a.IsActive).ToListAsync();

    public Task<Asset?> GetByIdAsync(int id) =>
        _context.Assets.FirstOrDefaultAsync(a => a.Id == id);

    public Task<Asset?> GetBySymbolAsync(string symbol) =>
        _context.Assets.FirstOrDefaultAsync(a => a.Symbol == symbol);

    public void Add(Asset asset) => _context.Assets.Add(asset);

    public void Update(Asset asset) => _context.Assets.Update(asset);

    public void Remove(Asset asset) => _context.Assets.Remove(asset);
}
