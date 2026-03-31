using System.Security.Cryptography;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using FundingRateArb.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class ConnectivityTestServiceTests
{
    private const string AdminUserId = "admin-user-1";
    private const string TargetUserId = "target-user-1";
    private const int TestExchangeId = 1;

    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IExchangeConnectorFactory> _mockConnectorFactory = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext = new();
    private readonly Mock<ILogger<ConnectivityTestService>> _mockLogger = new();
    private readonly Mock<IExchangeRepository> _mockExchangeRepo = new();
    private readonly Mock<IUserExchangeCredentialRepository> _mockCredentialRepo = new();
    private readonly Mock<IHubClients<IDashboardClient>> _mockHubClients = new();
    private readonly Mock<IDashboardClient> _mockDashboardClient = new();
    private readonly ConnectivityTestService _sut;

    public ConnectivityTestServiceTests()
    {
        // Clear cooldowns between tests to prevent cross-test interference
        ConnectivityTestService.ClearCooldowns();

        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
        _mockUow.Setup(u => u.UserCredentials).Returns(_mockCredentialRepo.Object);

        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
        _mockHubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockDashboardClient.Object);
        _mockDashboardClient
            .Setup(d => d.ReceiveConnectivityLog(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        _sut = new ConnectivityTestService(
            _mockUserSettings.Object,
            _mockConnectorFactory.Object,
            _mockUow.Object,
            _mockHubContext.Object,
            _mockLogger.Object);
    }

    private static Exchange CreateTestExchange(bool isDataOnly = false) => new()
    {
        Id = TestExchangeId,
        Name = "Hyperliquid",
        ApiBaseUrl = "https://api.hyperliquid.xyz",
        WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
        IsDataOnly = isDataOnly,
        IsActive = true
    };

    private static UserExchangeCredential CreateTestCredential(bool isActive = true) => new()
    {
        Id = 1,
        UserId = TargetUserId,
        ExchangeId = TestExchangeId,
        EncryptedWalletAddress = "encrypted-wallet",
        EncryptedPrivateKey = "encrypted-key",
        IsActive = isActive
    };

    private void SetupExchangeAndCredential(Exchange exchange, UserExchangeCredential? credential = null)
    {
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(TestExchangeId)).ReturnsAsync(exchange);

        if (credential is not null)
        {
            _mockCredentialRepo
                .Setup(r => r.GetByUserAndExchangeAsync(TargetUserId, TestExchangeId))
                .ReturnsAsync(credential);

            _mockUserSettings
                .Setup(u => u.DecryptCredential(credential))
                .Returns((null, null, "wallet-addr", "private-key", null, null));
        }
        else
        {
            _mockCredentialRepo
                .Setup(r => r.GetByUserAndExchangeAsync(TargetUserId, TestExchangeId))
                .ReturnsAsync((UserExchangeCredential?)null);
        }
    }

    private Mock<IExchangeConnector> CreateMockConnector(
        decimal balance = 100m,
        bool openSuccess = true,
        bool closeSuccess = true,
        string? openError = null,
        string? closeError = null,
        bool balanceThrows = false)
    {
        var mock = new Mock<IExchangeConnector>();

        if (balanceThrows)
        {
            mock.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Connection refused"));
        }
        else
        {
            mock.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(balance);
        }

        mock.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = openSuccess,
                OrderId = openSuccess ? "order-123" : null,
                Error = openError,
                FilledPrice = openSuccess ? 3000m : 0m,
                FilledQuantity = openSuccess ? 0.00167m : 0m
            });

        mock.Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = closeSuccess,
                OrderId = closeSuccess ? "close-456" : null,
                Error = closeError
            });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mock.Object);

        return mock;
    }

    [Fact]
    public async Task SuccessfulRoundTrip_ReturnsPassAndLogsAllSteps()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeTrue();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().BeNull();
        result.Balance.Should().BeNull("success path should not expose balance in JSON response");

        // Verify SignalR log messages were sent (at least balance, open, wait, close steps)
        _mockDashboardClient.Verify(
            d => d.ReceiveConnectivityLog("Hyperliquid", It.IsAny<string>()),
            Times.AtLeast(4));

        // Verify logs target the admin group, not the target user group
        _mockHubClients.Verify(c => c.Group($"user-{AdminUserId}"), Times.AtLeastOnce);
        _mockHubClients.Verify(c => c.Group($"user-{TargetUserId}"), Times.Never);
    }

    [Fact]
    public async Task BalanceCheckFailure_ReturnsFailWithError()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector(balanceThrows: true);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        // Sanitized: error message should not leak raw exception details
        result.Error.Should().Contain("Balance check failed");
        result.Error.Should().NotContain("Connection refused");
        result.Balance.Should().BeNull();
    }

    [Fact]
    public async Task OpenFailure_ReturnsFailAndDoesNotAttemptClose()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        var mockConnector = CreateMockConnector(openSuccess: false, openError: "Insufficient margin");

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        // Sanitized: should not contain raw SDK error
        result.Error.Should().Contain("Open failed");
        result.Error.Should().NotContain("Insufficient margin");
        result.Balance.Should().Be(100m);

        // Verify ClosePositionAsync was never called
        mockConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CloseFailure_RetriesOnce_ThenReturnsStrandedPositionError()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "order-123",
                FilledPrice = 3000m,
                FilledQuantity = 0.00167m
            });
        // Both close attempts fail
        mockConnector.Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "Position not found" });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("STRANDED POSITION");
        result.Balance.Should().Be(100m);

        // Verify close was called exactly twice (initial + retry)
        mockConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task DataOnlyExchange_ReturnsFailWithSkipMessage()
    {
        var exchange = CreateTestExchange(isDataOnly: true);
        SetupExchangeAndCredential(exchange);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("Data-only");
    }

    [Fact]
    public async Task NoCredentialsForExchange_ReturnsFailWithMessage()
    {
        var exchange = CreateTestExchange();
        SetupExchangeAndCredential(exchange, credential: null);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("credentials", "should mention missing credentials");

        // Verify SignalR log was sent for early-exit path
        _mockDashboardClient.Verify(
            d => d.ReceiveConnectivityLog(It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExchangeNotFound_ReturnsFailWithUnknownExchangeName()
    {
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(TestExchangeId))
            .ReturnsAsync((Exchange?)null);
        _mockCredentialRepo
            .Setup(r => r.GetByUserAndExchangeAsync(TargetUserId, TestExchangeId))
            .ReturnsAsync((UserExchangeCredential?)null);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Unknown");
        result.Error.Should().Contain("Exchange not found");
    }

    [Fact]
    public async Task InactiveCredential_ReturnsFailWithMessage()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential(isActive: false);
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(TestExchangeId)).ReturnsAsync(exchange);
        _mockCredentialRepo
            .Setup(r => r.GetByUserAndExchangeAsync(TargetUserId, TestExchangeId))
            .ReturnsAsync(credential);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("credentials");

        // Verify SignalR log was sent for early-exit path
        _mockDashboardClient.Verify(
            d => d.ReceiveConnectivityLog(It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ConnectorFactoryReturnsNull_ReturnsFailWithInvalidCredentials()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        // Override connector factory to return null
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync((IExchangeConnector?)null);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("invalid credentials");
    }

    [Fact]
    public async Task UnexpectedExceptionDuringOpen_ReturnsFail()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected SDK error"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("Unexpected error");
        // Sanitized: should NOT contain raw exception message
        result.Error.Should().NotContain("Unexpected SDK error");
    }

    [Fact]
    public async Task RunTest_WithinCooldown_ReturnsFailWithCooldownMessage()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        // First call should succeed
        var firstResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);
        firstResult.Success.Should().BeTrue();

        // Second call within cooldown period should be rate-limited
        var secondResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);
        secondResult.Success.Should().BeFalse();
        secondResult.Error.Should().Contain("Rate limited");
    }

    [Fact]
    public async Task RunTest_CooldownKeyScopedToTargetUser_NotAdmin()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        // First admin runs test for target:exchange — should succeed
        var firstResult = await _sut.RunTestAsync("admin-1", TargetUserId, TestExchangeId);
        firstResult.Success.Should().BeTrue();

        // Second admin runs test for same target:exchange — should be rate-limited
        // because cooldown key is scoped to targetUserId:exchangeId, not adminUserId
        var secondResult = await _sut.RunTestAsync("admin-2", TargetUserId, TestExchangeId);
        secondResult.Success.Should().BeFalse();
        secondResult.Error.Should().Contain("Rate limited");
    }

    [Fact]
    public async Task ConnectorFactoryReturnsNull_DoesNotConsumeCooldown()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        // First call: factory returns null (connector creation fails)
        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync((IExchangeConnector?)null);

        var firstResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);
        firstResult.Success.Should().BeFalse();
        firstResult.Error.Should().Contain("invalid credentials");

        // Second call: factory returns valid connector — should proceed (not rate-limited)
        CreateMockConnector();

        var secondResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);
        secondResult.Success.Should().BeTrue("factory failure should not have consumed the cooldown slot");
    }

    [Fact]
    public async Task CloseFailure_RetrySucceeds_ReturnsPass()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "order-123",
                FilledPrice = 3000m,
                FilledQuantity = 0.00167m
            });
        // First close fails, second succeeds
        mockConnector.SetupSequence(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = false, Error = "Temporary error" })
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "close-789" });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeTrue();

        // Verify close was called exactly twice (initial + retry)
        mockConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunTest_SuccessfulOpen_DoesNotExposeOrderDetails()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeTrue();

        // Verify the SignalR log for the open step does not contain order details
        _mockDashboardClient.Verify(
            d => d.ReceiveConnectivityLog("Hyperliquid",
                It.Is<string>(msg => msg.Contains("OrderId") || msg.Contains("Price") || msg.Contains("Qty") || msg.Contains("Quantity"))),
            Times.Never,
            "SignalR log messages should not expose order details");
    }

    [Fact]
    public async Task RunTest_CancellationDuringSettlementWait_ReturnsFail()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var cts = new CancellationTokenSource();

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        // Cancel the token inside PlaceMarketOrderAsync callback so cancellation
        // occurs after open succeeds but before the settlement Task.Delay completes
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 5m, 1, It.IsAny<CancellationToken>()))
            .Returns<string, Side, decimal, int, CancellationToken>((_, _, _, _, _) =>
            {
                cts.Cancel();
                return Task.FromResult(new OrderResultDto
                {
                    Success = true,
                    OrderId = "order-123",
                    FilledPrice = 3000m,
                    FilledQuantity = 0.00167m
                });
            });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, cts.Token);

        result.Success.Should().BeFalse();

        // ClosePositionAsync should never be called since cancellation occurs during settlement wait
        mockConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DecryptCredentialThrows_ReturnsFail()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(TestExchangeId)).ReturnsAsync(exchange);
        _mockCredentialRepo
            .Setup(r => r.GetByUserAndExchangeAsync(TargetUserId, TestExchangeId))
            .ReturnsAsync(credential);

        // Mock DecryptCredential to throw CryptographicException (e.g., corrupted encryption key)
        _mockUserSettings
            .Setup(u => u.DecryptCredential(credential))
            .Throws(new CryptographicException("Bad data"));

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("Unexpected error");

        // CreateForUserAsync should never be called since decryption failed
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }
}
