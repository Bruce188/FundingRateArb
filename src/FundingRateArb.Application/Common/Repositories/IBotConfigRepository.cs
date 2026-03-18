using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IBotConfigRepository
{
    Task<BotConfiguration> GetActiveAsync();
    void Update(BotConfiguration config);
}
