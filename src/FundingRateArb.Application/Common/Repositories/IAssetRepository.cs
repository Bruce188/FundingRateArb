using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IAssetRepository
{
    Task<List<Asset>> GetAllAsync();
    Task<List<Asset>> GetActiveAsync();
    Task<Asset?> GetByIdAsync(int id);
    Task<Asset?> GetBySymbolAsync(string symbol);
    void Add(Asset asset);
    void Update(Asset asset);
    void Remove(Asset asset);
}
