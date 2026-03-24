using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public class UserSettingsService : IUserSettingsService
{
    private readonly IUnitOfWork _uow;
    private readonly IApiKeyVault _vault;

    public UserSettingsService(IUnitOfWork uow, IApiKeyVault vault)
    {
        _uow = uow;
        _vault = vault;
    }

    // --- Credentials ---

    public async Task SaveCredentialAsync(string userId, int exchangeId,
        string? apiKey, string? apiSecret, string? walletAddress, string? privateKey)
    {
        var existing = await _uow.UserCredentials.GetByUserAndExchangeAsync(userId, exchangeId);

        if (existing is not null)
        {
            existing.EncryptedApiKey = apiKey is not null ? _vault.Encrypt(apiKey) : null;
            existing.EncryptedApiSecret = apiSecret is not null ? _vault.Encrypt(apiSecret) : null;
            existing.EncryptedWalletAddress = walletAddress is not null ? _vault.Encrypt(walletAddress) : null;
            existing.EncryptedPrivateKey = privateKey is not null ? _vault.Encrypt(privateKey) : null;
            existing.IsActive = true;
            existing.LastUpdatedAt = DateTime.UtcNow;
            _uow.UserCredentials.Update(existing);
        }
        else
        {
            var credential = new UserExchangeCredential
            {
                UserId = userId,
                ExchangeId = exchangeId,
                EncryptedApiKey = apiKey is not null ? _vault.Encrypt(apiKey) : null,
                EncryptedApiSecret = apiSecret is not null ? _vault.Encrypt(apiSecret) : null,
                EncryptedWalletAddress = walletAddress is not null ? _vault.Encrypt(walletAddress) : null,
                EncryptedPrivateKey = privateKey is not null ? _vault.Encrypt(privateKey) : null,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _uow.UserCredentials.Add(credential);
        }

        await _uow.SaveAsync();
    }

    public Task<UserExchangeCredential?> GetCredentialAsync(string userId, int exchangeId) =>
        _uow.UserCredentials.GetByUserAndExchangeAsync(userId, exchangeId);

    public Task<List<UserExchangeCredential>> GetActiveCredentialsAsync(string userId) =>
        _uow.UserCredentials.GetActiveByUserAsync(userId);

    public Task<List<UserExchangeCredential>> GetAllCredentialsAsync(string userId) =>
        _uow.UserCredentials.GetAllByUserAsync(userId);

    public async Task DeleteCredentialAsync(string userId, int exchangeId)
    {
        var credential = await _uow.UserCredentials.GetByUserAndExchangeAsync(userId, exchangeId);
        if (credential is not null)
        {
            _uow.UserCredentials.Delete(credential);
            await _uow.SaveAsync();
        }
    }

    public (string? ApiKey, string? ApiSecret, string? WalletAddress, string? PrivateKey) DecryptCredential(
        UserExchangeCredential credential)
    {
        return (
            credential.EncryptedApiKey is not null ? _vault.Decrypt(credential.EncryptedApiKey) : null,
            credential.EncryptedApiSecret is not null ? _vault.Decrypt(credential.EncryptedApiSecret) : null,
            credential.EncryptedWalletAddress is not null ? _vault.Decrypt(credential.EncryptedWalletAddress) : null,
            credential.EncryptedPrivateKey is not null ? _vault.Decrypt(credential.EncryptedPrivateKey) : null
        );
    }

    // --- Configuration ---

    public async Task<UserConfiguration> GetOrCreateConfigAsync(string userId)
    {
        var config = await _uow.UserConfigurations.GetByUserAsync(userId);
        if (config is not null)
        {
            return config;
        }

        config = new UserConfiguration
        {
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _uow.UserConfigurations.Add(config);
        await _uow.SaveAsync();
        return config;
    }

    public async Task UpdateConfigAsync(string userId, UserConfiguration config)
    {
        config.LastUpdatedAt = DateTime.UtcNow;
        _uow.UserConfigurations.Update(config);
        await _uow.SaveAsync();
    }

    // --- Preferences ---

    public Task<List<Exchange>> GetAvailableExchangesAsync() =>
        _uow.Exchanges.GetActiveAsync();

    public Task<List<Asset>> GetAvailableAssetsAsync() =>
        _uow.Assets.GetActiveAsync();

    public Task<List<int>> GetUserEnabledExchangeIdsAsync(string userId) =>
        _uow.UserPreferences.GetEnabledExchangeIdsAsync(userId);

    public Task<List<int>> GetUserEnabledAssetIdsAsync(string userId) =>
        _uow.UserPreferences.GetEnabledAssetIdsAsync(userId);

    public async Task SetExchangePreferenceAsync(string userId, int exchangeId, bool isEnabled)
    {
        await _uow.UserPreferences.SetExchangePreferenceAsync(userId, exchangeId, isEnabled);
        await _uow.SaveAsync();
    }

    public async Task SetAssetPreferenceAsync(string userId, int assetId, bool isEnabled)
    {
        await _uow.UserPreferences.SetAssetPreferenceAsync(userId, assetId, isEnabled);
        await _uow.SaveAsync();
    }

    /// <summary>
    /// Saves all exchange and asset preferences in a single database round-trip.
    /// </summary>
    public async Task SavePreferencesAsync(string userId,
        Dictionary<int, bool> exchangePreferences,
        Dictionary<int, bool> assetPreferences)
    {
        foreach (var (exchangeId, isEnabled) in exchangePreferences)
        {
            await _uow.UserPreferences.SetExchangePreferenceAsync(userId, exchangeId, isEnabled);
        }

        foreach (var (assetId, isEnabled) in assetPreferences)
        {
            await _uow.UserPreferences.SetAssetPreferenceAsync(userId, assetId, isEnabled);
        }

        await _uow.SaveAsync();
    }

    public async Task InitializeDefaultsForNewUserAsync(string userId)
    {
        await _uow.UserPreferences.InitializeDefaultsAsync(userId);
        await _uow.SaveAsync();
    }

    // --- Validation ---

    public async Task<bool> HasValidCredentialsAsync(string userId)
    {
        var active = await _uow.UserCredentials.GetActiveByUserAsync(userId);
        return active.Count >= 2;
    }
}
