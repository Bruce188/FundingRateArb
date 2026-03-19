using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Repositories;

public class ExchangeAssetConfigRepository : IExchangeAssetConfigRepository
{
    private readonly AppDbContext _context;

    public ExchangeAssetConfigRepository(AppDbContext context) => _context = context;

    public Task<ExchangeAssetConfig?> GetByExchangeAndAssetAsync(int exchangeId, int assetId) =>
        _context.ExchangeAssetConfigs
            .FirstOrDefaultAsync(c => c.ExchangeId == exchangeId && c.AssetId == assetId);

    public Task<List<ExchangeAssetConfig>> GetByExchangeAsync(int exchangeId) =>
        _context.ExchangeAssetConfigs
            .Where(c => c.ExchangeId == exchangeId)
            .ToListAsync();

    public void Add(ExchangeAssetConfig config) => _context.ExchangeAssetConfigs.Add(config);

    public void Update(ExchangeAssetConfig config) => _context.ExchangeAssetConfigs.Update(config);
}
