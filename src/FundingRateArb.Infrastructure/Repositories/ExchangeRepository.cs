using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FundingRateArb.Infrastructure.Repositories;

public class ExchangeRepository : IExchangeRepository
{
    private const string CacheKey = "Exchanges:Active";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public ExchangeRepository(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public Task<List<Exchange>> GetAllAsync() =>
        _context.Exchanges.AsNoTracking().ToListAsync();

    public async Task<List<Exchange>> GetActiveAsync()
    {
        if (_cache.TryGetValue(CacheKey, out List<Exchange>? cached) && cached is not null)
        {
            return cached.Select(ShallowCopy).ToList();
        }

        var result = await _context.Exchanges.AsNoTracking().Where(e => e.IsActive).ToListAsync();
        _cache.Set(CacheKey, result, new MemoryCacheEntryOptions { SlidingExpiration = CacheDuration });
        return result.Select(ShallowCopy).ToList();
    }

    public Task<Exchange?> GetByIdAsync(int id) =>
        _context.Exchanges.FirstOrDefaultAsync(e => e.Id == id);

    public Task<Exchange?> GetByNameAsync(string name) =>
        _context.Exchanges.FirstOrDefaultAsync(e => e.Name == name);

    public void Add(Exchange exchange) => _context.Exchanges.Add(exchange);

    public void Update(Exchange exchange) => _context.Exchanges.Update(exchange);

    public void Remove(Exchange exchange) => _context.Exchanges.Remove(exchange);

    public void InvalidateCache() => _cache.Remove(CacheKey);

    private static Exchange ShallowCopy(Exchange src) => new()
    {
        Id = src.Id,
        Name = src.Name,
        ApiBaseUrl = src.ApiBaseUrl,
        WsBaseUrl = src.WsBaseUrl,
        FundingInterval = src.FundingInterval,
        FundingIntervalHours = src.FundingIntervalHours,
        FundingSettlementType = src.FundingSettlementType,
        SupportsSubAccounts = src.SupportsSubAccounts,
        IsActive = src.IsActive,
        TakerFeeRate = src.TakerFeeRate,
        Description = src.Description,
    };
}
