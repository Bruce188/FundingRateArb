using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class ConnectorLifecycleManagerTests
{
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly ConnectorLifecycleManager _sut;

    public ConnectorLifecycleManagerTests()
    {
        _sut = new ConnectorLifecycleManager(
            _mockFactory.Object, _mockUserSettings.Object,
            NullLogger<ConnectorLifecycleManager>.Instance);
    }

    // ── CreateUserConnectorsAsync ──────────────────────────────────────────

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsConnectors_WhenCredentialsExist()
    {
        // Arrange
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));

        var mockLong = new Mock<IExchangeConnector>();
        var mockShort = new Mock<IExchangeConnector>();
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockShort.Object);

        // Act
        var (longConn, shortConn, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert
        error.Should().BeNull();
        longConn.Should().Be(mockLong.Object);
        shortConn.Should().Be(mockShort.Object);
    }

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsError_WhenLongCredentialMissing()
    {
        // Arrange — only short credential exists
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { shortCred });

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert
        error.Should().Contain("No credentials found for Hyperliquid");
    }

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsError_WhenShortCredentialMissing()
    {
        // Arrange — only long credential exists
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred });

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert
        error.Should().Contain("No credentials found for Lighter");
    }

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsError_WhenUserIdIsNullOrEmpty()
    {
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("", "Hyperliquid", "Lighter");

        error.Should().Contain("User ID is required");
    }

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsError_WhenDecryptionFails()
    {
        // Arrange
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Throws(new InvalidOperationException("Decryption failed"));

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert
        error.Should().Contain("Credential validation failed");
    }

    [Fact]
    public async Task CreateUserConnectorsAsync_DisposesLongConnector_WhenShortCreationFails()
    {
        // Arrange
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));

        var mockLong = new Mock<IExchangeConnector>();
        var disposable = mockLong.As<IDisposable>();
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("Connection failed"));

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert
        error.Should().Contain("Exchange connection failed");
        disposable.Verify(d => d.Dispose(), Times.Once);
    }

    // ── Factory-returns-null paths (NB4) ────────────────────────────────────

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsError_WhenFactoryReturnsNullLongConnector()
    {
        // Arrange
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));

        var mockShort = new Mock<IExchangeConnector>();
        var shortDisposable = mockShort.As<IDisposable>();
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((IExchangeConnector?)null);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockShort.Object);

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert — error returned, short connector disposed
        error.Should().Contain("invalid credentials");
        shortDisposable.Verify(d => d.Dispose(), Times.Once);
    }

    [Fact]
    public async Task CreateUserConnectorsAsync_ReturnsError_WhenFactoryReturnsNullShortConnector()
    {
        // Arrange
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));

        var mockLong = new Mock<IExchangeConnector>();
        var longDisposable = mockLong.As<IDisposable>();
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockLong.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((IExchangeConnector?)null);

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert — error returned, long connector disposed
        error.Should().Contain("invalid credentials");
        longDisposable.Verify(d => d.Dispose(), Times.Once);
    }

    // ── Short decryption failure disposes long connector (NB5) ────────────

    [Fact]
    public async Task CreateUserConnectorsAsync_DisposesLongConnector_WhenShortDecryptionFails()
    {
        // Arrange — long decryption succeeds, short decryption throws
        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });

        var callCount = 0;
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    return ("key", "secret", "wallet", "pk", (string?)null, (string?)null);
                throw new InvalidOperationException("Short decryption failed");
            });

        var mockLong = new Mock<IExchangeConnector>();
        var longDisposable = mockLong.As<IDisposable>();
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(mockLong.Object);

        // Act
        var (_, _, error) = await _sut.CreateUserConnectorsAsync("user1", "Hyperliquid", "Lighter");

        // Assert — long connector disposed after short decryption fails
        error.Should().NotBeNull();
        longDisposable.Verify(d => d.Dispose(), Times.Once);
    }

    // ── GetCachedMaxLeverageAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetCachedMaxLeverageAsync_ReturnsCachedValue_WithinTTL()
    {
        // Arrange
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        mockConnector
            .Setup(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()))
            .ReturnsAsync(20);

        // Act — call twice
        var first = await _sut.GetCachedMaxLeverageAsync(mockConnector.Object, "ETH", CancellationToken.None);
        var second = await _sut.GetCachedMaxLeverageAsync(mockConnector.Object, "ETH", CancellationToken.None);

        // Assert — API called only once (cached)
        first.Should().Be(20);
        second.Should().Be(20);
        mockConnector.Verify(c => c.GetMaxLeverageAsync("ETH", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── GetCachedMaxLeverageAsync — null return (NB6) ──────────────────────

    [Fact]
    public async Task GetCachedMaxLeverageAsync_ReturnsNull_WhenConnectorReturnsNull()
    {
        // Arrange — connector returns null (asset not found)
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        mockConnector
            .Setup(c => c.GetMaxLeverageAsync("UNKNOWN", It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        // Act — call twice
        var first = await _sut.GetCachedMaxLeverageAsync(mockConnector.Object, "UNKNOWN", CancellationToken.None);
        var second = await _sut.GetCachedMaxLeverageAsync(mockConnector.Object, "UNKNOWN", CancellationToken.None);

        // Assert — both null, connector queried twice (no caching of nulls)
        first.Should().BeNull();
        second.Should().BeNull();
        mockConnector.Verify(c => c.GetMaxLeverageAsync("UNKNOWN", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── DisposeConnectorAsync ─────────────────────────────────────────────

    [Fact]
    public async Task DisposeConnectorAsync_HandlesNullConnector()
    {
        // Should not throw
        await ConnectorLifecycleManager.DisposeConnectorAsync(null);
    }

    [Fact]
    public async Task DisposeConnectorAsync_CallsAsyncDisposable_WhenAvailable()
    {
        var mockConnector = new Mock<IExchangeConnector>();
        var asyncDisposable = mockConnector.As<IAsyncDisposable>();
        asyncDisposable.Setup(d => d.DisposeAsync()).Returns(ValueTask.CompletedTask);

        await ConnectorLifecycleManager.DisposeConnectorAsync(mockConnector.Object);

        asyncDisposable.Verify(d => d.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeConnectorAsync_CallsSyncDispose_WhenNotAsyncDisposable()
    {
        // Arrange — connector implements IDisposable only, not IAsyncDisposable
        var mockConnector = new Mock<IExchangeConnector>();
        var disposable = mockConnector.As<IDisposable>();

        // Act
        await ConnectorLifecycleManager.DisposeConnectorAsync(mockConnector.Object);

        // Assert — sync Dispose() called
        disposable.Verify(d => d.Dispose(), Times.Once);
    }

    // ── WrapForDryRun ─────────────────────────────────────────────────────

    [Fact]
    public void WrapForDryRun_ReturnsDryRunConnectorWrapperInstances()
    {
        var mockLong = new Mock<IExchangeConnector>();
        mockLong.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        var mockShort = new Mock<IExchangeConnector>();
        mockShort.Setup(c => c.ExchangeName).Returns("Lighter");

        var (wrappedLong, wrappedShort) = _sut.WrapForDryRun(mockLong.Object, mockShort.Object);

        wrappedLong.Should().BeOfType<DryRunConnectorWrapper>();
        wrappedShort.Should().BeOfType<DryRunConnectorWrapper>();
    }
}
