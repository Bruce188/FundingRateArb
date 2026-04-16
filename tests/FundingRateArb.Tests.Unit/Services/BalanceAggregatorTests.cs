using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class BalanceAggregatorTests
{
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IExchangeConnectorFactory> _mockConnectorFactory = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly Mock<ILogger<BalanceAggregator>> _mockLogger = new();
    private readonly BalanceAggregator _sut;

    public BalanceAggregatorTests()
    {
        _sut = new BalanceAggregator(
            _mockUserSettings.Object,
            _mockConnectorFactory.Object,
            _cache,
            _mockLogger.Object);
    }

    [Fact]
    public async Task GetBalanceSnapshot_ReturnsSumOfAllExchangeBalances()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
            new() { Id = 2, ExchangeId = 2, Exchange = new Exchange { Id = 2, Name = "Lighter" }, EncryptedPrivateKey = "y" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockHl = new Mock<IExchangeConnector>();
        mockHl.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(50m);
        var mockLighter = new Mock<IExchangeConnector>();
        mockLighter.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(30m);

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockHl.Object);
        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Lighter", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockLighter.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(80m);
        result.Balances.Should().HaveCount(2);
        result.Balances.Should().Contain(b => b.ExchangeName == "Hyperliquid" && b.AvailableUsdc == 50m);
        result.Balances.Should().Contain(b => b.ExchangeName == "Lighter" && b.AvailableUsdc == 30m);
    }

    [Fact]
    public async Task GetBalanceSnapshot_ReturnsCachedResult_WithinTtl()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(100m);
        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        // First call
        await _sut.GetBalanceSnapshotAsync("user1");
        // Second call — should use cache
        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(100m);
        // Connector should only have been called once (cached second time)
        _mockUserSettings.Verify(u => u.GetActiveCredentialsAsync("user1"), Times.Once);
    }

    [Fact]
    public async Task GetBalanceSnapshot_OneExchangeFails_UsesZeroForThatExchange()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
            new() { Id = 2, ExchangeId = 2, Exchange = new Exchange { Id = 2, Name = "Lighter" }, EncryptedPrivateKey = "y" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockHl = new Mock<IExchangeConnector>();
        mockHl.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("timeout"));
        var mockLighter = new Mock<IExchangeConnector>();
        mockLighter.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(30m);

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockHl.Object);
        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Lighter", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockLighter.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(30m);
        result.Balances.Should().Contain(b => b.ExchangeName == "Hyperliquid" && b.AvailableUsdc == 0m);
        result.Balances.Should().Contain(b => b.ExchangeName == "Lighter" && b.AvailableUsdc == 30m);
    }

    [Fact]
    public async Task GetBalanceSnapshot_AllFail_ReturnsZeroTotal()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("down"));
        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(0m);
    }

    [Fact]
    public async Task GetBalanceSnapshot_NoCredentials_ReturnsEmptySnapshot()
    {
        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1"))
            .ReturnsAsync(new List<UserExchangeCredential>());

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(0m);
        result.Balances.Should().BeEmpty();
    }

    [Fact]
    public async Task FailedExchange_PopulatesErrorMessage()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API key expired"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().NotBeNull();
        result.Balances[0].ErrorMessage.Should().Be("Hyperliquid: balance fetch failed", "raw exception messages must be sanitized and include exchange name");
        result.Balances[0].AvailableUsdc.Should().Be(0m);
    }

    [Fact]
    public async Task SuccessfulExchange_HasNullError()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().BeNull();
        result.Balances[0].AvailableUsdc.Should().Be(100m);
    }

    [Fact]
    public async Task NullConnector_PopulatesCredentialsNotConfiguredError()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync((IExchangeConnector?)null);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Be("Credentials not configured");
        result.Balances[0].AvailableUsdc.Should().Be(0m);
    }

    [Fact]
    public async Task HttpRequestException_SanitizedToExchangeUnreachable()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused: internal-host.exchange.com:443"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Be("Hyperliquid: unreachable", "HttpRequestException must be sanitized to hide internal details");
        result.Balances[0].AvailableUsdc.Should().Be(0m);
    }

    [Fact]
    public async Task GetBalanceSnapshot_ExcludesFailedExchangeFromTotal()
    {
        // When one exchange fails, TotalAvailableUsdc should explicitly exclude
        // it (filter by ErrorMessage is null), not just happen to add 0.
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
            new() { Id = 2, ExchangeId = 2, Exchange = new Exchange { Id = 2, Name = "Lighter" }, EncryptedPrivateKey = "y" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockHl = new Mock<IExchangeConnector>();
        mockHl.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(5000m);
        var mockLighter = new Mock<IExchangeConnector>();
        mockLighter.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("timeout"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockHl.Object);
        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Lighter", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockLighter.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(5000m, "failed exchange must be excluded from total");
        result.Balances.Should().HaveCount(2);
        result.Balances.Should().Contain(b => b.ExchangeName == "Lighter" && b.ErrorMessage != null);

        // Structural property: TotalAvailableUsdc must equal the sum of non-error balances
        result.Balances.Where(b => b.ErrorMessage is null).Sum(b => b.AvailableUsdc)
            .Should().Be(result.TotalAvailableUsdc, "TotalAvailableUsdc must only sum non-error balances");
    }

    [Fact]
    public async Task FailedExchange_ErrorMessageIncludesExchangeName()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Aster" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("something unexpected"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Aster", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().StartWith("Aster:", "error message must include exchange name");
    }

    [Theory]
    [InlineData("Invalid API-key, IP, or permissions for action")]
    [InlineData("Error code -2015: invalid key")]
    [InlineData("Unauthorized access denied")]
    public async Task AuthError_ProducesSpecificMessage(string errorMessage)
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Aster" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(errorMessage));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Aster", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Be("Aster: API key invalid or expired");
    }

    [Fact]
    public async Task NoRecognizedQuoteAsset_ProducesSpecificMessage()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Aster" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No recognized quote asset (USDT/USDC/USD) in balance response. Assets found: BNB, ETH"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Aster", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Be("Aster: no recognized quote asset (USDT/USDC/USD) found");
    }

    [Fact]
    public async Task AuthError_PersistsLastErrorOnCredential()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Binance" }, EncryptedApiKey = "k" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Invalid API-key, IP, or permissions for action"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Binance", "key", "secret", null, null, null, null))
            .ReturnsAsync(mockConnector.Object);

        await _sut.GetBalanceSnapshotAsync("user1");

        _mockUserSettings.Verify(
            u => u.UpdateCredentialErrorAsync("user1", 1, It.Is<string>(s => s.Contains("API key invalid")), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AuthErrorBackoff_SkipsCredentialWithRecentError()
    {
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 1, ExchangeId = 1,
                Exchange = new Exchange { Id = 1, Name = "Binance" },
                EncryptedApiKey = "k",
                LastError = "Binance: API key invalid or expired",
                LastErrorAt = DateTime.UtcNow.AddMinutes(-2),
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Contain("API key invalid");
        result.Balances[0].AvailableUsdc.Should().Be(0m);

        // Connector should never have been created
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task AuthErrorBackoff_RetriesAfterExpiry()
    {
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 1, ExchangeId = 1,
                Exchange = new Exchange { Id = 1, Name = "Binance" },
                EncryptedApiKey = "k",
                LastError = "Binance: API key invalid or expired",
                LastErrorAt = DateTime.UtcNow.AddMinutes(-10), // Past the 5-minute backoff
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Binance", "key", "secret", null, null, null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].AvailableUsdc.Should().Be(100m);
        result.Balances[0].ErrorMessage.Should().BeNull();

        // Connector SHOULD have been created — backoff expired
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync("Binance", "key", "secret", null, null, null, null),
            Times.Once);
    }

    [Fact]
    public async Task SuccessfulFetch_ClearsLastError()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Binance" }, EncryptedApiKey = "k" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Binance", "key", "secret", null, null, null, null))
            .ReturnsAsync(mockConnector.Object);

        await _sut.GetBalanceSnapshotAsync("user1");

        _mockUserSettings.Verify(
            u => u.UpdateCredentialErrorAsync("user1", 1, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NonAuthError_DoesNotPersistToCredential()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        await _sut.GetBalanceSnapshotAsync("user1");

        // Non-auth errors (HttpRequestException) should NOT be persisted to the credential
        _mockUserSettings.Verify(
            u => u.UpdateCredentialErrorAsync("user1", 1, It.Is<string>(s => s != null), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ArgumentException_WithCredentialsMessage_ClassifiedAsAuthError()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Aster" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("V1 credentials not provided"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Aster", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Be("Aster: API key invalid or expired");
    }

    [Fact]
    public async Task ArgumentException_WithGenericMessage_FallsThrough()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Aster" }, EncryptedWalletAddress = "x" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("some other error"));

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Aster", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Be("Aster: balance fetch failed");
    }

    [Fact]
    public void GetAuthErrorBackoff_FirstFailure_Returns5Minutes()
    {
        var backoff = BalanceAggregator.GetAuthErrorBackoff(1);
        backoff.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void GetAuthErrorBackoff_ThirdFailure_Returns20Minutes()
    {
        var backoff = BalanceAggregator.GetAuthErrorBackoff(3);
        backoff.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void GetAuthErrorBackoff_FifthFailure_Returns60Minutes()
    {
        var backoff = BalanceAggregator.GetAuthErrorBackoff(5);
        backoff.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void GetAuthErrorBackoff_HighFailures_CappedAt60Minutes()
    {
        var backoff = BalanceAggregator.GetAuthErrorBackoff(10);
        backoff.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public async Task AuthError_WithHighConsecutiveFailures_UsesLongerBackoff()
    {
        // Credential with ConsecutiveFailures=3 and error 15min ago
        // Backoff for 3 failures = 20 min, so 15 min is still within backoff -> should be skipped
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 1, ExchangeId = 1,
                Exchange = new Exchange { Id = 1, Name = "Binance" },
                EncryptedApiKey = "k",
                LastError = "Binance: API key invalid or expired",
                LastErrorAt = DateTime.UtcNow.AddMinutes(-15),
                ConsecutiveFailures = 3,
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Contain("API key invalid");
        // Connector should NOT have been created — still in backoff
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task AuthError_WithLowConsecutiveFailures_RetriesEarlier()
    {
        // Credential with ConsecutiveFailures=1 and error 3min ago
        // Backoff for 1 failure = 5 min, so 3 min is within backoff -> should be skipped
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 1, ExchangeId = 1,
                Exchange = new Exchange { Id = 1, Name = "Binance" },
                EncryptedApiKey = "k",
                LastError = "Binance: API key invalid or expired",
                LastErrorAt = DateTime.UtcNow.AddMinutes(-3),
                ConsecutiveFailures = 1,
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.Balances.Should().HaveCount(1);
        result.Balances[0].ErrorMessage.Should().Contain("API key invalid");
        // Connector should NOT have been created — still in 5-min backoff
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task GetBalanceSnapshot_AuthError_SetsUnavailableNotZero()
    {
        // Arrange — use distinct userId to avoid cache pollution from other tests
        const string userId = "user-auth-unavailable";
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 1, ExchangeId = 1,
                Exchange = new Exchange { Id = 1, Name = "Binance" },
                EncryptedApiKey = "k",
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Invalid API-key, IP, or permissions for action"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Binance", "key", "secret", null, null, null, null))
            .ReturnsAsync(mockConnector.Object);

        // Act
        var result = await _sut.GetBalanceSnapshotAsync(userId);

        // Assert
        result.Balances.Should().HaveCount(1);
        result.Balances[0].IsUnavailable.Should().BeTrue();
        result.Balances[0].AvailableUsdc.Should().Be(0m);
        result.TotalAvailableUsdc.Should().Be(0m, "unavailable exchange must be excluded from total");
    }

    [Fact]
    public async Task GetBalanceSnapshot_TransientError_FallsBackToLastKnownGood()
    {
        // Arrange — use a fresh BalanceAggregator with its own cache to control LKG population
        const string userId = "user-lkg-fallback";
        var freshCache = new MemoryCache(new MemoryCacheOptions());
        var sut = new BalanceAggregator(_mockUserSettings.Object, _mockConnectorFactory.Object, freshCache, _mockLogger.Object);

        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 10, ExchangeId = 10,
                Exchange = new Exchange { Id = 10, Name = "Aster" },
                EncryptedApiKey = "k",
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        var sequence = mockConnector.SetupSequence(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()));
        sequence.ReturnsAsync(100m).ThrowsAsync(new HttpRequestException("timeout"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Aster", "key", "secret", null, null, null, null))
            .ReturnsAsync(mockConnector.Object);

        // First call: populates LKG cache
        await sut.GetBalanceSnapshotAsync(userId);

        // Expire the main balance cache so next call re-fetches
        freshCache.Remove($"balance:{userId}");

        // Act: second call hits the transient error path
        var result = await sut.GetBalanceSnapshotAsync(userId);

        // Assert: LKG fallback is used
        result.Balances.Should().HaveCount(1);
        result.Balances[0].AvailableUsdc.Should().Be(100m);
        result.Balances[0].IsUnavailable.Should().BeFalse();
        result.Balances[0].ErrorMessage.Should().Contain("cached balance");

        // N2: transient error must NOT trigger UpdateCredentialErrorAsync
        _mockUserSettings.Verify(
            u => u.UpdateCredentialErrorAsync(userId, It.IsAny<int>(), It.Is<string>(s => s != null), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetBalanceSnapshot_TransientError_NoPriorBalance_SetsUnavailable()
    {
        // Arrange — use distinct userId; no prior successful call so no LKG
        const string userId = "user-no-lkg";
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 20, ExchangeId = 20,
                Exchange = new Exchange { Id = 20, Name = "Hyperliquid" },
                EncryptedWalletAddress = "w",
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "w", (string?)null, (string?)null, (string?)null));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "w", null, null, null))
            .ReturnsAsync(mockConnector.Object);

        // Act
        var result = await _sut.GetBalanceSnapshotAsync(userId);

        // Assert
        result.Balances.Should().HaveCount(1);
        result.Balances[0].IsUnavailable.Should().BeTrue();
        result.Balances[0].AvailableUsdc.Should().Be(0m);
    }

    [Fact]
    public async Task GetBalanceSnapshot_TransientError_StaleBalance_SetsIsStale()
    {
        // Arrange — use a fresh cache and pre-populate LKG with old timestamp
        const string userId = "user-stale-lkg";
        var freshCache = new MemoryCache(new MemoryCacheOptions());
        var sut = new BalanceAggregator(_mockUserSettings.Object, _mockConnectorFactory.Object, freshCache, _mockLogger.Object);

        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 30, ExchangeId = 30,
                Exchange = new Exchange { Id = 30, Name = "Lighter" },
                EncryptedPrivateKey = "pk",
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, (string?)null, "pk", (string?)null, (string?)null));

        // Manually pre-populate LKG cache with a stale (>10 min ago) timestamp
        var staleTimestamp = DateTime.UtcNow.AddMinutes(-15);
        freshCache.Set($"balance-lkg:{userId}:30", (Value: 75m, FetchedAt: staleTimestamp), TimeSpan.FromHours(1));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector
            .Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("timeout"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Lighter", null, null, null, "pk", null, null))
            .ReturnsAsync(mockConnector.Object);

        // Act
        var result = await sut.GetBalanceSnapshotAsync(userId);

        // Assert
        result.Balances.Should().HaveCount(1);
        result.Balances[0].IsStale.Should().BeTrue();
        result.Balances[0].IsUnavailable.Should().BeFalse();
        result.Balances[0].AvailableUsdc.Should().Be(75m);
    }

    [Fact]
    public async Task GetBalanceSnapshot_AuthErrorBackoff_SetsUnavailable()
    {
        // Arrange — credential in auth-error backoff
        const string userId = "user-backoff-unavailable";
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 40, ExchangeId = 40,
                Exchange = new Exchange { Id = 40, Name = "Binance" },
                EncryptedApiKey = "k",
                LastError = "Binance: API key invalid or expired",
                LastErrorAt = DateTime.UtcNow.AddMinutes(-1),
                ConsecutiveFailures = 1,
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);

        // Act
        var result = await _sut.GetBalanceSnapshotAsync(userId);

        // Assert: backoff-skip path must now set IsUnavailable
        result.Balances.Should().HaveCount(1);
        result.Balances[0].IsUnavailable.Should().BeTrue();
        result.Balances[0].AvailableUsdc.Should().Be(0m);
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task GetBalanceSnapshot_TotalAvailableUsdc_IncludesStaleButNotUnavailable()
    {
        // Arrange — three exchanges: healthy (50m), stale fallback (30m), unavailable (auth error)
        const string userId = "user-mixed-total";
        var freshCache = new MemoryCache(new MemoryCacheOptions());
        var sut = new BalanceAggregator(_mockUserSettings.Object, _mockConnectorFactory.Object, freshCache, _mockLogger.Object);

        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 50, ExchangeId = 50, Exchange = new Exchange { Id = 50, Name = "Hyperliquid" }, EncryptedWalletAddress = "w" },
            new() { Id = 51, ExchangeId = 51, Exchange = new Exchange { Id = 51, Name = "Lighter" }, EncryptedPrivateKey = "pk" },
            new() { Id = 52, ExchangeId = 52, Exchange = new Exchange { Id = 52, Name = "Binance" }, EncryptedApiKey = "k" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 50)))
            .Returns(((string?)null, (string?)null, "w", (string?)null, (string?)null, (string?)null));
        _mockUserSettings.Setup(u => u.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 51)))
            .Returns(((string?)null, (string?)null, (string?)null, "pk", (string?)null, (string?)null));
        _mockUserSettings.Setup(u => u.DecryptCredential(It.Is<UserExchangeCredential>(c => c.ExchangeId == 52)))
            .Returns(("key", "secret", (string?)null, (string?)null, (string?)null, (string?)null));

        // Hyperliquid: healthy fetch returns 50m
        var mockHl = new Mock<IExchangeConnector>();
        mockHl.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(50m);
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "w", null, null, null))
            .ReturnsAsync(mockHl.Object);

        // Lighter: transient error, with stale LKG of 30m pre-populated
        var staleTimestamp = DateTime.UtcNow.AddMinutes(-15);
        freshCache.Set($"balance-lkg:{userId}:51", (Value: 30m, FetchedAt: staleTimestamp), TimeSpan.FromHours(1));
        var mockLighter = new Mock<IExchangeConnector>();
        mockLighter.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("timeout"));
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Lighter", null, null, null, "pk", null, null))
            .ReturnsAsync(mockLighter.Object);

        // Binance: auth error → unavailable
        var mockBinance = new Mock<IExchangeConnector>();
        mockBinance.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Invalid API-key, IP, or permissions for action"));
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Binance", "key", "secret", null, null, null, null))
            .ReturnsAsync(mockBinance.Object);

        // Act
        var result = await sut.GetBalanceSnapshotAsync(userId);

        // Assert
        result.Balances.Should().HaveCount(3);
        result.Balances.Should().Contain(b => b.ExchangeName == "Hyperliquid" && b.IsUnavailable == false && b.IsStale == false);
        result.Balances.Should().Contain(b => b.ExchangeName == "Lighter" && b.IsStale == true && b.IsUnavailable == false && b.AvailableUsdc == 30m);
        result.Balances.Should().Contain(b => b.ExchangeName == "Binance" && b.IsUnavailable == true);
        result.TotalAvailableUsdc.Should().Be(80m, "total must include healthy (50m) + stale (30m) but exclude unavailable");
    }

    [Fact]
    public async Task GetBalanceSnapshot_ConnectorNull_SetsUnavailable()
    {
        // Arrange — B1: null connector (credentials not configured) must set IsUnavailable
        const string userId = "user-null-connector";
        var creds = new List<UserExchangeCredential>
        {
            new()
            {
                Id = 60, ExchangeId = 60,
                Exchange = new Exchange { Id = 60, Name = "Lighter" },
                EncryptedPrivateKey = "pk",
            },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync(userId)).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, (string?)null, "pk", (string?)null, (string?)null));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Lighter", null, null, null, "pk", null, null))
            .ReturnsAsync((IExchangeConnector?)null);

        // Act
        var result = await _sut.GetBalanceSnapshotAsync(userId);

        // Assert: null connector must produce IsUnavailable=true so downstream guards block trading
        result.Balances.Should().HaveCount(1);
        result.Balances[0].IsUnavailable.Should().BeTrue();
        result.Balances[0].AvailableUsdc.Should().Be(0m);
        result.TotalAvailableUsdc.Should().Be(0m, "unavailable exchange must be excluded from total");
    }

    [Fact]
    public async Task MixedScenario_OneSuccessOneNull_CorrectTotalAndErrors()
    {
        var creds = new List<UserExchangeCredential>
        {
            new() { Id = 1, ExchangeId = 1, Exchange = new Exchange { Id = 1, Name = "Hyperliquid" }, EncryptedWalletAddress = "x" },
            new() { Id = 2, ExchangeId = 2, Exchange = new Exchange { Id = 2, Name = "Lighter" }, EncryptedPrivateKey = "y" },
        };

        _mockUserSettings.Setup(u => u.GetActiveCredentialsAsync("user1")).ReturnsAsync(creds);
        _mockUserSettings.Setup(u => u.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(((string?)null, (string?)null, "wallet", "key", (string?)null, (string?)null));

        var mockHl = new Mock<IExchangeConnector>();
        mockHl.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(500m);

        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet", "key", null, null))
            .ReturnsAsync(mockHl.Object);
        _mockConnectorFactory.Setup(f => f.CreateForUserAsync("Lighter", null, null, "wallet", "key", null, null))
            .ReturnsAsync((IExchangeConnector?)null);

        var result = await _sut.GetBalanceSnapshotAsync("user1");

        result.TotalAvailableUsdc.Should().Be(500m, "only the successful exchange contributes to total");
        result.Balances.Should().HaveCount(2);
        result.Balances.Should().Contain(b => b.ExchangeName == "Hyperliquid" && b.AvailableUsdc == 500m && b.ErrorMessage == null);
        result.Balances.Should().Contain(b => b.ExchangeName == "Lighter" && b.AvailableUsdc == 0m && b.ErrorMessage == "Credentials not configured");

        // Structural property: TotalAvailableUsdc must equal the sum of non-error balances
        result.Balances.Where(b => b.ErrorMessage is null).Sum(b => b.AvailableUsdc)
            .Should().Be(result.TotalAvailableUsdc, "TotalAvailableUsdc must only sum non-error balances");
    }
}
