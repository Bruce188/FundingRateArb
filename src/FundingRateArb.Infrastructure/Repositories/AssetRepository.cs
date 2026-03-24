using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Infrastructure.Repositories;

public class AssetRepository : IAssetRepository
{
    private const string CacheKey = "Assets:Active";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public AssetRepository(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public Task<List<Asset>> GetAllAsync() =>
        _context.Assets.AsNoTracking().ToListAsync();

    public async Task<List<Asset>> GetActiveAsync()
    {
        if (_cache.TryGetValue(CacheKey, out List<Asset>? cached) && cached is not null)
        {
            return cached.Select(ShallowCopy).ToList();
        }

        var result = await _context.Assets.AsNoTracking().Where(a => a.IsActive).ToListAsync();
        _cache.Set(CacheKey, result, new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });
        return result.Select(ShallowCopy).ToList();
    }

    public Task<Asset?> GetByIdAsync(int id) =>
        _context.Assets.FirstOrDefaultAsync(a => a.Id == id);

    public Task<Asset?> GetBySymbolAsync(string symbol) =>
        _context.Assets.FirstOrDefaultAsync(a => a.Symbol == symbol);

    public void Add(Asset asset) => _context.Assets.Add(asset);

    public void Update(Asset asset) => _context.Assets.Update(asset);

    public void Remove(Asset asset) => _context.Assets.Remove(asset);

    public void InvalidateCache() => _cache.Remove(CacheKey);

    private static Asset ShallowCopy(Asset src) => new()
    {
        Id = src.Id,
        Symbol = src.Symbol,
        Name = src.Name,
        IsActive = src.IsActive,
    };
}
