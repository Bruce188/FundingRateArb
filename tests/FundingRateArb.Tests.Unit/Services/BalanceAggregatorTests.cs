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
