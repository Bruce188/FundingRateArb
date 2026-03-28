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
        result.Balances[0].ErrorMessage.Should().Contain("API key expired");
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
}
