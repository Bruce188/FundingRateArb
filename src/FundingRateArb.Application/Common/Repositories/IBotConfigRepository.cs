using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IBotConfigRepository
{
    Task<BotConfiguration> GetActiveAsync();
    Task<BotConfiguration> GetActiveTrackedAsync();
    void Update(BotConfiguration config);
}
