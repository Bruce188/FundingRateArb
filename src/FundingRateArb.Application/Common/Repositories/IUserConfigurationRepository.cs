using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IUserConfigurationRepository
{
    Task<UserConfiguration?> GetByUserAsync(string userId);
    void Add(UserConfiguration config);
    void Update(UserConfiguration config);
}
