using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class UserSettingsServiceTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IApiKeyVault> _mockVault = new();
    private readonly Mock<IUserExchangeCredentialRepository> _mockCredentials = new();
    private readonly Mock<IUserConfigurationRepository> _mockConfigurations = new();
    private readonly Mock<IUserPreferenceRepository> _mockPreferences = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly UserSettingsService _sut;

    private const string UserId = "test-user-id";

    public UserSettingsServiceTests()
    {
        _mockUow.Setup(u => u.UserCredentials).Returns(_mockCredentials.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockConfigurations.Object);
        _mockUow.Setup(u => u.UserPreferences).Returns(_mockPreferences.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _sut = new UserSettingsService(_mockUow.Object, _mockVault.Object);
    }

    // --- Credential Tests ---

    [Fact]
    public async Task SaveCredentialAsync_NewCredential_EncryptsAndAdds()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetByUserAndExchangeAsync(UserId, 1))
            .ReturnsAsync((UserExchangeCredential?)null);

        _mockVault.Setup(v => v.Encrypt("my-api-key")).Returns("encrypted-key");
        _mockVault.Setup(v => v.Encrypt("my-api-secret")).Returns("encrypted-secret");

        // Act
        await _sut.SaveCredentialAsync(UserId, 1, "my-api-key", "my-api-secret", null, null);

        // Assert
        _mockVault.Verify(v => v.Encrypt("my-api-key"), Times.Once);
        _mockVault.Verify(v => v.Encrypt("my-api-secret"), Times.Once);
        _mockCredentials.Verify(r => r.Add(It.Is<UserExchangeCredential>(c =>
            c.UserId == UserId &&
            c.ExchangeId == 1 &&
            c.EncryptedApiKey == "encrypted-key" &&
            c.EncryptedApiSecret == "encrypted-secret" &&
            c.EncryptedWalletAddress == null &&
            c.EncryptedPrivateKey == null &&
            c.IsActive)), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task SaveCredentialAsync_ExistingCredential_EncryptsAndUpdates()
    {
        // Arrange
        var existing = new UserExchangeCredential
        {
            Id = 42,
            UserId = UserId,
            ExchangeId = 1,
            EncryptedApiKey = "old-encrypted-key"
        };
        _mockCredentials
            .Setup(r => r.GetByUserAndExchangeAsync(UserId, 1))
            .ReturnsAsync(existing);

        _mockVault.Setup(v => v.Encrypt("new-key")).Returns("encrypted-new-key");
        _mockVault.Setup(v => v.Encrypt("new-secret")).Returns("encrypted-new-secret");

        // Act
        await _sut.SaveCredentialAsync(UserId, 1, "new-key", "new-secret", null, null);

        // Assert
        existing.EncryptedApiKey.Should().Be("encrypted-new-key");
        existing.EncryptedApiSecret.Should().Be("encrypted-new-secret");
        _mockCredentials.Verify(r => r.Update(existing), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task SaveCredentialAsync_WithWalletAndPrivateKey_EncryptsAll()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetByUserAndExchangeAsync(UserId, 1))
            .ReturnsAsync((UserExchangeCredential?)null);

        _mockVault.Setup(v => v.Encrypt(It.IsAny<string>()))
            .Returns<string>(s => $"enc({s})");

        // Act
        await _sut.SaveCredentialAsync(UserId, 1, "key", "secret", "0xWallet", "privKey");

        // Assert
        _mockCredentials.Verify(r => r.Add(It.Is<UserExchangeCredential>(c =>
            c.EncryptedApiKey == "enc(key)" &&
            c.EncryptedApiSecret == "enc(secret)" &&
            c.EncryptedWalletAddress == "enc(0xWallet)" &&
            c.EncryptedPrivateKey == "enc(privKey)")), Times.Once);
    }

    [Fact]
    public void DecryptCredential_DecryptsAllFields()
    {
        // Arrange
        var credential = new UserExchangeCredential
        {
            EncryptedApiKey = "enc-key",
            EncryptedApiSecret = "enc-secret",
            EncryptedWalletAddress = "enc-wallet",
            EncryptedPrivateKey = "enc-priv"
        };
        _mockVault.Setup(v => v.Decrypt("enc-key")).Returns("api-key");
        _mockVault.Setup(v => v.Decrypt("enc-secret")).Returns("api-secret");
        _mockVault.Setup(v => v.Decrypt("enc-wallet")).Returns("wallet-addr");
        _mockVault.Setup(v => v.Decrypt("enc-priv")).Returns("priv-key");

        // Act
        var (apiKey, apiSecret, walletAddress, privateKey) = _sut.DecryptCredential(credential);

        // Assert
        apiKey.Should().Be("api-key");
        apiSecret.Should().Be("api-secret");
        walletAddress.Should().Be("wallet-addr");
        privateKey.Should().Be("priv-key");
    }

    [Fact]
    public void DecryptCredential_WithNullFields_ReturnsNulls()
    {
        // Arrange
        var credential = new UserExchangeCredential
        {
            EncryptedApiKey = "enc-key",
            EncryptedApiSecret = null,
            EncryptedWalletAddress = null,
            EncryptedPrivateKey = null
        };
        _mockVault.Setup(v => v.Decrypt("enc-key")).Returns("api-key");

        // Act
        var (apiKey, apiSecret, walletAddress, privateKey) = _sut.DecryptCredential(credential);

        // Assert
        apiKey.Should().Be("api-key");
        apiSecret.Should().BeNull();
        walletAddress.Should().BeNull();
        privateKey.Should().BeNull();
    }

    [Fact]
    public async Task DeleteCredentialAsync_ExistingCredential_DeletesAndSaves()
    {
        // Arrange
        var credential = new UserExchangeCredential { Id = 1, UserId = UserId, ExchangeId = 1 };
        _mockCredentials
            .Setup(r => r.GetByUserAndExchangeAsync(UserId, 1))
            .ReturnsAsync(credential);

        // Act
        await _sut.DeleteCredentialAsync(UserId, 1);

        // Assert
        _mockCredentials.Verify(r => r.Delete(credential), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task DeleteCredentialAsync_NonExistent_DoesNothing()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetByUserAndExchangeAsync(UserId, 99))
            .ReturnsAsync((UserExchangeCredential?)null);

        // Act
        await _sut.DeleteCredentialAsync(UserId, 99);

        // Assert
        _mockCredentials.Verify(r => r.Delete(It.IsAny<UserExchangeCredential>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Never);
    }

    // --- Configuration Tests ---

    [Fact]
    public async Task GetOrCreateConfigAsync_ExistingConfig_ReturnsIt()
    {
        // Arrange
        var config = new UserConfiguration { Id = 1, UserId = UserId, TotalCapitalUsdc = 500m };
        _mockConfigurations
            .Setup(r => r.GetByUserAsync(UserId))
            .ReturnsAsync(config);

        // Act
        var result = await _sut.GetOrCreateConfigAsync(UserId);

        // Assert
        result.Should().BeSameAs(config);
        _mockConfigurations.Verify(r => r.Add(It.IsAny<UserConfiguration>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateConfigAsync_NoConfig_CreatesWithDefaults()
    {
        // Arrange
        _mockConfigurations
            .Setup(r => r.GetByUserAsync(UserId))
            .ReturnsAsync((UserConfiguration?)null);

        // Act
        var result = await _sut.GetOrCreateConfigAsync(UserId);

        // Assert
        result.UserId.Should().Be(UserId);
        result.TotalCapitalUsdc.Should().Be(100m); // default
        result.DefaultLeverage.Should().Be(5); // default
        result.MaxConcurrentPositions.Should().Be(3); // default
        _mockConfigurations.Verify(r => r.Add(It.Is<UserConfiguration>(c =>
            c.UserId == UserId)), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task UpdateConfigAsync_SetsLastUpdatedAndSaves()
    {
        // Arrange
        var config = new UserConfiguration
        {
            Id = 1,
            UserId = UserId,
            TotalCapitalUsdc = 200m,
            LastUpdatedAt = null
        };

        // Act
        await _sut.UpdateConfigAsync(UserId, config);

        // Assert
        config.LastUpdatedAt.Should().NotBeNull();
        _mockConfigurations.Verify(r => r.Update(config), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    // --- Preference Tests ---

    [Fact]
    public async Task SetExchangePreferenceAsync_DelegatesToRepoAndSaves()
    {
        // Act
        await _sut.SetExchangePreferenceAsync(UserId, 1, true);

        // Assert
        _mockPreferences.Verify(r => r.SetExchangePreferenceAsync(UserId, 1, true), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task SetExchangePreferenceAsync_Disable_DelegatesToRepoAndSaves()
    {
        // Act
        await _sut.SetExchangePreferenceAsync(UserId, 2, false);

        // Assert
        _mockPreferences.Verify(r => r.SetExchangePreferenceAsync(UserId, 2, false), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task SetAssetPreferenceAsync_DelegatesToRepoAndSaves()
    {
        // Act
        await _sut.SetAssetPreferenceAsync(UserId, 5, false);

        // Assert
        _mockPreferences.Verify(r => r.SetAssetPreferenceAsync(UserId, 5, false), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task InitializeDefaultsForNewUserAsync_DelegatesToRepoAndSaves()
    {
        // Act
        await _sut.InitializeDefaultsForNewUserAsync(UserId);

        // Assert
        _mockPreferences.Verify(r => r.InitializeDefaultsAsync(UserId), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task GetAvailableExchangesAsync_ReturnsActiveExchanges()
    {
        // Arrange
        var exchanges = new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid", IsActive = true },
            new() { Id = 2, Name = "Lighter", IsActive = true }
        };
        _mockExchanges.Setup(r => r.GetActiveAsync()).ReturnsAsync(exchanges);

        // Act
        var result = await _sut.GetAvailableExchangesAsync();

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAvailableAssetsAsync_ReturnsActiveAssets()
    {
        // Arrange
        var assets = new List<Asset>
        {
            new() { Id = 1, Symbol = "BTC", Name = "Bitcoin", IsActive = true },
            new() { Id = 2, Symbol = "ETH", Name = "Ethereum", IsActive = true }
        };
        _mockAssets.Setup(r => r.GetActiveAsync()).ReturnsAsync(assets);

        // Act
        var result = await _sut.GetAvailableAssetsAsync();

        // Assert
        result.Should().HaveCount(2);
    }

    // --- Validation Tests ---

    [Fact]
    public async Task HasValidCredentialsAsync_ZeroExchanges_ReturnsFalse()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetActiveByUserAsync(UserId))
            .ReturnsAsync(new List<UserExchangeCredential>());

        // Act
        var result = await _sut.HasValidCredentialsAsync(UserId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasValidCredentialsAsync_OneExchange_ReturnsFalse()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetActiveByUserAsync(UserId))
            .ReturnsAsync(new List<UserExchangeCredential>
            {
                new() { Id = 1, UserId = UserId, ExchangeId = 1, IsActive = true }
            });

        // Act
        var result = await _sut.HasValidCredentialsAsync(UserId);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasValidCredentialsAsync_TwoExchanges_ReturnsTrue()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetActiveByUserAsync(UserId))
            .ReturnsAsync(new List<UserExchangeCredential>
            {
                new() { Id = 1, UserId = UserId, ExchangeId = 1, IsActive = true },
                new() { Id = 2, UserId = UserId, ExchangeId = 2, IsActive = true }
            });

        // Act
        var result = await _sut.HasValidCredentialsAsync(UserId);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasValidCredentialsAsync_ThreeExchanges_ReturnsTrue()
    {
        // Arrange
        _mockCredentials
            .Setup(r => r.GetActiveByUserAsync(UserId))
            .ReturnsAsync(new List<UserExchangeCredential>
            {
                new() { Id = 1, UserId = UserId, ExchangeId = 1, IsActive = true },
                new() { Id = 2, UserId = UserId, ExchangeId = 2, IsActive = true },
                new() { Id = 3, UserId = UserId, ExchangeId = 3, IsActive = true }
            });

        // Act
        var result = await _sut.HasValidCredentialsAsync(UserId);

        // Assert
        result.Should().BeTrue();
    }
}
