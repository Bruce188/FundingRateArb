using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface IUserPreferenceRepository
{
    Task<List<UserExchangePreference>> GetExchangePreferencesAsync(string userId);
    Task<List<UserAssetPreference>> GetAssetPreferencesAsync(string userId);
    Task<List<int>> GetEnabledExchangeIdsAsync(string userId);
    Task<List<int>> GetEnabledAssetIdsAsync(string userId);
    Task SetExchangePreferenceAsync(string userId, int exchangeId, bool isEnabled);
    Task SetAssetPreferenceAsync(string userId, int assetId, bool isEnabled);
    Task InitializeDefaultsAsync(string userId);
}
