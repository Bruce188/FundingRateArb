using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IExchangeAssetConfigRepository
{
    Task<ExchangeAssetConfig?> GetByExchangeAndAssetAsync(int exchangeId, int assetId);
    Task<List<ExchangeAssetConfig>> GetByExchangeAsync(int exchangeId);
    void Add(ExchangeAssetConfig config);
    void Update(ExchangeAssetConfig config);
}
