using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IUserSettingsService
{
    // Credentials
    Task SaveCredentialAsync(string userId, int exchangeId,
        string? apiKey, string? apiSecret, string? walletAddress, string? privateKey,
        string? subAccountAddress = null, string? apiKeyIndex = null);
    Task<UserExchangeCredential?> GetCredentialAsync(string userId, int exchangeId);
    Task<List<UserExchangeCredential>> GetActiveCredentialsAsync(string userId);
    Task<List<UserExchangeCredential>> GetAllCredentialsAsync(string userId);
    Task DeleteCredentialAsync(string userId, int exchangeId);
    (string? ApiKey, string? ApiSecret, string? WalletAddress, string? PrivateKey,
        string? SubAccountAddress, string? ApiKeyIndex) DecryptCredential(
        UserExchangeCredential credential);

    // Configuration
    Task<UserConfiguration> GetOrCreateConfigAsync(string userId);
    Task UpdateConfigAsync(string userId, UserConfiguration config);

    // Preferences
    Task<List<Exchange>> GetAvailableExchangesAsync();
    Task<List<int>> GetDataOnlyExchangeIdsAsync();
    Task<List<Asset>> GetAvailableAssetsAsync();
    Task<List<int>> GetUserEnabledExchangeIdsAsync(string userId);
    Task<List<int>> GetUserEnabledAssetIdsAsync(string userId);
    Task SetExchangePreferenceAsync(string userId, int exchangeId, bool isEnabled);
    Task SetAssetPreferenceAsync(string userId, int assetId, bool isEnabled);
    Task SavePreferencesAsync(string userId, Dictionary<int, bool> exchangePreferences, Dictionary<int, bool> assetPreferences);
    Task InitializeDefaultsForNewUserAsync(string userId);

    // Validation
    Task<bool> HasValidCredentialsAsync(string userId);
}
