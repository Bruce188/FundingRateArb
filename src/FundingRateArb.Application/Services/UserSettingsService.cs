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
        string? apiKey, string? apiSecret, string? walletAddress, string? privateKey,
        string? subAccountAddress = null, string? apiKeyIndex = null)
    {
        var existing = await _uow.UserCredentials.GetByUserAndExchangeAsync(userId, exchangeId);

        if (existing is not null)
        {
            existing.EncryptedApiKey = !string.IsNullOrEmpty(apiKey) ? _vault.Encrypt(apiKey) : existing.EncryptedApiKey;
            existing.EncryptedApiSecret = !string.IsNullOrEmpty(apiSecret) ? _vault.Encrypt(apiSecret) : existing.EncryptedApiSecret;
            existing.EncryptedWalletAddress = !string.IsNullOrEmpty(walletAddress) ? _vault.Encrypt(walletAddress) : existing.EncryptedWalletAddress;
            existing.EncryptedPrivateKey = !string.IsNullOrEmpty(privateKey) ? _vault.Encrypt(privateKey) : existing.EncryptedPrivateKey;
            existing.EncryptedSubAccountAddress = !string.IsNullOrEmpty(subAccountAddress) ? _vault.Encrypt(subAccountAddress) : existing.EncryptedSubAccountAddress;
            existing.EncryptedApiKeyIndex = !string.IsNullOrEmpty(apiKeyIndex) ? _vault.Encrypt(apiKeyIndex) : existing.EncryptedApiKeyIndex;
            existing.IsActive = true;
            existing.LastUpdatedAt = DateTime.UtcNow;
            existing.LastError = null;
            existing.LastErrorAt = null;
            _uow.UserCredentials.Update(existing);
        }
        else
        {
            var credential = new UserExchangeCredential
            {
                UserId = userId,
                ExchangeId = exchangeId,
                EncryptedApiKey = !string.IsNullOrEmpty(apiKey) ? _vault.Encrypt(apiKey) : null,
                EncryptedApiSecret = !string.IsNullOrEmpty(apiSecret) ? _vault.Encrypt(apiSecret) : null,
                EncryptedWalletAddress = !string.IsNullOrEmpty(walletAddress) ? _vault.Encrypt(walletAddress) : null,
                EncryptedPrivateKey = !string.IsNullOrEmpty(privateKey) ? _vault.Encrypt(privateKey) : null,
                EncryptedSubAccountAddress = !string.IsNullOrEmpty(subAccountAddress) ? _vault.Encrypt(subAccountAddress) : null,
                EncryptedApiKeyIndex = !string.IsNullOrEmpty(apiKeyIndex) ? _vault.Encrypt(apiKeyIndex) : null,
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

    public (string? ApiKey, string? ApiSecret, string? WalletAddress, string? PrivateKey,
        string? SubAccountAddress, string? ApiKeyIndex) DecryptCredential(
        UserExchangeCredential credential)
    {
        return (
            credential.EncryptedApiKey is not null ? _vault.Decrypt(credential.EncryptedApiKey) : null,
            credential.EncryptedApiSecret is not null ? _vault.Decrypt(credential.EncryptedApiSecret) : null,
            credential.EncryptedWalletAddress is not null ? _vault.Decrypt(credential.EncryptedWalletAddress) : null,
            credential.EncryptedPrivateKey is not null ? _vault.Decrypt(credential.EncryptedPrivateKey) : null,
            credential.EncryptedSubAccountAddress is not null ? _vault.Decrypt(credential.EncryptedSubAccountAddress) : null,
            credential.EncryptedApiKeyIndex is not null ? _vault.Decrypt(credential.EncryptedApiKeyIndex) : null
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

    public async Task<List<int>> GetDataOnlyExchangeIdsAsync()
    {
        var exchanges = await _uow.Exchanges.GetActiveAsync();
        return exchanges.Where(e => e.IsDataOnly).Select(e => e.Id).ToList();
    }

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

    // --- Error tracking ---

    public async Task UpdateCredentialErrorAsync(string userId, int exchangeId, string? error, CancellationToken ct = default)
    {
        var credential = await _uow.UserCredentials.GetByUserAndExchangeAsync(userId, exchangeId);
        if (credential is null)
        {
            return;
        }

        // Short-circuit: skip DB write only for repeated success clears (null → null).
        // Repeated errors must still increment ConsecutiveFailures for escalating backoff.
        if (credential.LastError == error && error is null)
        {
            return;
        }

        if (error is not null)
        {
            credential.ConsecutiveFailures++;
        }
        else
        {
            credential.ConsecutiveFailures = 0;
        }

        credential.LastError = error;
        credential.LastErrorAt = error is not null ? DateTime.UtcNow : null;
        _uow.UserCredentials.Update(credential);
        await _uow.SaveAsync(ct);
    }

    // --- Usage tracking ---

    public async Task TouchLastUsedAsync(string userId, int exchangeId, CancellationToken ct = default)
    {
        var credential = await _uow.UserCredentials.GetByUserAndExchangeAsync(userId, exchangeId);
        if (credential is null)
        {
            return;
        }

        credential.LastUsedAt = DateTime.UtcNow;
        _uow.UserCredentials.Update(credential);
        await _uow.SaveAsync(ct);
    }
}
