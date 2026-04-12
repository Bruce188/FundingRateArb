using System.Collections.Concurrent;
using System.Reflection;
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
    private readonly Mock<IPositionRepository> _mockPositionRepo = new();
    private readonly Mock<IHubClients<IDashboardClient>> _mockHubClients = new();
    private readonly Mock<IDashboardClient> _mockDashboardClient = new();
    private readonly ConnectivityTestService _sut;

    public ConnectivityTestServiceTests()
    {
        // Clear cooldowns between tests to prevent cross-test interference
        ConnectivityTestService.ClearCooldowns();

        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
        _mockUow.Setup(u => u.UserCredentials).Returns(_mockCredentialRepo.Object);
        // Default: no open positions (active-trade lock should not block)
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockPositionRepo
            .Setup(r => r.GetOpenByUserAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ArbitragePosition>());

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

        mock.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
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

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

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
        // Error message now surfaces actual exchange error for diagnosis
        result.Error.Should().Contain("Balance check failed:");
        result.Balance.Should().BeNull();
    }

    [Fact]
    public async Task OpenFailure_ReturnsFailAndDoesNotAttemptClose()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        var mockConnector = CreateMockConnector(openSuccess: false, openError: "Insufficient margin");

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeFalse();
        // Error message now surfaces actual exchange error for diagnosis
        result.Error.Should().Contain("Open failed:");
        result.Error.Should().Contain("Insufficient margin");
        result.Balance.Should().BeNull("balance should not be exposed on failure paths");

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
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
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

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("STRANDED POSITION");
        result.Balance.Should().BeNull("balance should not be exposed on failure paths");

        // Verify close was called exactly twice (initial + retry) with CancellationToken.None
        mockConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.Is<CancellationToken>(t => t == CancellationToken.None)),
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
        result.Balance.Should().BeNull();
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
        result.Balance.Should().BeNull();

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
        result.Balance.Should().BeNull();
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
        result.Balance.Should().BeNull();

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
        result.Balance.Should().BeNull();
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
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected SDK error"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeFalse();
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("Unexpected error");
        // Sanitized: should NOT contain raw exception message
        result.Error.Should().NotContain("Unexpected SDK error");
        result.Balance.Should().BeNull();
    }

    [Fact]
    public async Task RunTest_WithinCooldown_ReturnsFailWithCooldownMessage()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        // First call should succeed
        var firstResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);
        firstResult.Success.Should().BeTrue();

        // Second call within cooldown period should be rate-limited
        var secondResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);
        secondResult.Success.Should().BeFalse();
        secondResult.Error.Should().Contain("Rate limited");
        // NB5: Early cooldown uses "Unknown" since exchange lookup is skipped
        secondResult.ExchangeName.Should().Be("Unknown");
        secondResult.Balance.Should().BeNull();
    }

    [Fact]
    public async Task RunTest_CooldownKeyScopedToTargetUser_NotAdmin()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        // First admin runs test for target:exchange — should succeed
        var firstResult = await _sut.RunTestAsync("admin-1", TargetUserId, TestExchangeId, dryRun: false);
        firstResult.Success.Should().BeTrue();

        // Second admin runs test for same target:exchange — should be rate-limited
        // because cooldown key is scoped to targetUserId:exchangeId, not adminUserId
        var secondResult = await _sut.RunTestAsync("admin-2", TargetUserId, TestExchangeId, dryRun: false);
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

        var firstResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);
        firstResult.Success.Should().BeFalse();
        firstResult.Error.Should().Contain("invalid credentials");

        // Second call: factory returns valid connector — should proceed (not rate-limited)
        CreateMockConnector();

        var secondResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);
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
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
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

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue();

        // Verify close was called exactly twice (initial + retry) with CancellationToken.None
        mockConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.Is<CancellationToken>(t => t == CancellationToken.None)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunTest_SuccessfulOpen_DoesNotExposeOrderDetails()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue();

        // Verify the SignalR log for the open step does not contain order details
        _mockDashboardClient.Verify(
            d => d.ReceiveConnectivityLog("Hyperliquid",
                It.Is<string>(msg => msg.Contains("OrderId") || msg.Contains("Price") || msg.Contains("Qty") || msg.Contains("Quantity"))),
            Times.Never,
            "SignalR log messages should not expose order details");
    }

    [Fact]
    public async Task RunTest_CancellationDuringSettlementWait_StillAttemptsClose()
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
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
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
        // Close succeeds after cancellation-triggered attempt
        mockConnector.Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "close-456" });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false, cts.Token);

        result.Success.Should().BeTrue("close should succeed even after cancellation during settlement");

        // After B1 fix: close IS attempted even when cancellation occurs during settlement wait
        mockConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
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
        result.Balance.Should().BeNull();

        // CreateForUserAsync should never be called since decryption failed
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RunTestAsync_WhitespaceAdminUserId_Throws()
    {
        var act = async () => await _sut.RunTestAsync("   ", TargetUserId, TestExchangeId);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("adminUserId");
    }

    [Fact]
    public async Task RunTestAsync_WhitespaceTargetUserId_Throws()
    {
        var act = async () => await _sut.RunTestAsync(AdminUserId, "   ", TestExchangeId);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("targetUserId");
    }

    [Fact]
    public async Task RunTest_PostCreationCooldownRace_ReturnsRateLimited()
    {
        // Exercise the TryAdd failure path (pre-connector-creation gate).
        // Seed a non-expired cooldown before RunTestAsync so the early check catches it.
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        // Simulate concurrent request that already claimed the cooldown slot
        ConnectivityTestService.SeedCooldown(TargetUserId, TestExchangeId, DateTime.UtcNow);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Rate limited");
        // Early cooldown check fires before exchange lookup, so name is "Unknown"
        result.ExchangeName.Should().Be("Unknown");
        result.Balance.Should().BeNull();
    }

    [Fact]
    public async Task RunTest_ExpiredConcurrentCooldown_ProceedsSuccessfully()
    {
        // NB4: Exercise the "expired entry from concurrent path — overwrite" branch.
        // Seed an expired cooldown before RunTestAsync so TryAdd fails but expiry
        // check passes, causing the overwrite path to execute.
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        var expiredTime = DateTime.UtcNow - ConnectivityTestService.CooldownPeriod - TimeSpan.FromSeconds(1);

        // Simulate concurrent request that left an expired entry
        ConnectivityTestService.SeedCooldown(TargetUserId, TestExchangeId, expiredTime);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue("expired concurrent cooldown entry should be overwritten");

        // NB9: Confirm the overwrite populated the cooldown by making a second immediate call
        // (no ClearCooldowns) — it should be rate-limited
        var secondResult = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);
        secondResult.Success.Should().BeFalse();
        secondResult.Error.Should().Contain("Rate limited");
    }

    [Fact]
    public async Task RunTest_ForwardsAllCredentialFields()
    {
        // NB8: Verify correct forwarding when all six credential tuple fields are populated
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(TestExchangeId)).ReturnsAsync(exchange);
        _mockCredentialRepo
            .Setup(r => r.GetByUserAndExchangeAsync(TargetUserId, TestExchangeId))
            .ReturnsAsync(credential);

        _mockUserSettings
            .Setup(u => u.DecryptCredential(credential))
            .Returns(("api-key", "api-secret", null, null, "sub-account", "key-idx"));

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "o-1", FilledPrice = 3000m, FilledQuantity = 0.00167m });
        mockConnector.Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "c-1" });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", "api-key", "api-secret", null, null, "sub-account", "key-idx"))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue();

        // Verify CreateForUserAsync received exact credential values
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync("Hyperliquid", "api-key", "api-secret", null, null, "sub-account", "key-idx"),
            Times.Once);
    }

    [Fact]
    public async Task RunTest_DisposesConnector_AfterCompletion()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var mockDisposableConnector = new Mock<IExchangeConnector>();
        var mockDisposable = mockDisposableConnector.As<IDisposable>();
        mockDisposableConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        mockDisposableConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "o-1", FilledPrice = 3000m, FilledQuantity = 0.00167m });
        mockDisposableConnector.Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, OrderId = "c-1" });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockDisposableConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue();
        mockDisposable.Verify(d => d.Dispose(), Times.Once);
    }

    [Fact]
    public async Task RunTest_DisposesConnector_AfterFailure()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var mockDisposableConnector = new Mock<IExchangeConnector>();
        var mockDisposable = mockDisposableConnector.As<IDisposable>();
        mockDisposableConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockDisposableConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId);

        result.Success.Should().BeFalse();
        mockDisposable.Verify(d => d.Dispose(), Times.Once);
    }

    [Fact]
    public async Task RunTest_CancellationDuringCloseRetry_StillRetriesWithNoneToken()
    {
        // After B1 fix: close uses CancellationToken.None, so request cancellation
        // does not prevent close retry. Both close attempts proceed.
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        var cts = new CancellationTokenSource();

        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(100m);
        mockConnector.Setup(c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto
            {
                Success = true,
                OrderId = "order-123",
                FilledPrice = 3000m,
                FilledQuantity = 0.00167m
            });
        // Both close attempts fail (request cancellation no longer prevents retry)
        mockConnector.Setup(c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .Returns<string, Side, CancellationToken>((_, _, _) =>
            {
                cts.Cancel(); // Cancel request token — should not affect close retry
                return Task.FromResult(new OrderResultDto { Success = false, Error = "Timeout" });
            });

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mockConnector.Object);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false, cts.Token);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("STRANDED POSITION");
        result.Balance.Should().BeNull();
        // Both close attempts proceed despite request cancellation, using CancellationToken.None
        mockConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.Is<CancellationToken>(t => t == CancellationToken.None)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_SkipsTradeTest_WhenBalanceBelowMinimum()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        var mockConnector = CreateMockConnector(balance: 7.99m);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue("connectivity itself works, only margin is low");
        result.ExchangeName.Should().Be("Hyperliquid");
        result.Error.Should().Contain("trade test skipped");
        result.Error.Should().NotContain("Balance OK", "message should not say Balance OK when balance is insufficient");

        // PlaceMarketOrderAsync should NOT be called
        mockConnector.Verify(
            c => c.PlaceMarketOrderAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_ProceedsWithTradeTest_WhenBalanceAtMinimum()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        var mockConnector = CreateMockConnector(balance: 10.00m);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Success.Should().BeTrue();

        // PlaceMarketOrderAsync SHOULD be called (boundary: 10.00 >= 10.00)
        mockConnector.Verify(
            c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunTest_PurgeBranch_RemovesExpiredEntries()
    {
        // NB3: Exercise the periodic purge path that removes expired cooldown entries.
        // Seed multiple expired + fresh entries, force purge by setting _lastPurgeTicks to 0.
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateMockConnector();

        var expiredTime = DateTime.UtcNow - ConnectivityTestService.CooldownPeriod - TimeSpan.FromSeconds(10);
        var freshTime = DateTime.UtcNow;

        // Seed expired entries for other user/exchange combos
        ConnectivityTestService.SeedCooldown("expired-user-1", 99, expiredTime);
        ConnectivityTestService.SeedCooldown("expired-user-2", 98, expiredTime);
        // Seed a fresh entry for a different user/exchange
        ConnectivityTestService.SeedCooldown("fresh-user-1", 97, freshTime);

        // Force purge to trigger by setting _lastPurgeTicks to 0 via reflection
        var lastPurgeField = typeof(ConnectivityTestService)
            .GetField("_lastPurgeTicks", BindingFlags.NonPublic | BindingFlags.Static)!;
        lastPurgeField.SetValue(null, 0L);

        // RunTestAsync triggers purge check at the start
        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);
        result.Success.Should().BeTrue();

        // Verify expired entries were purged: calling RunTestAsync for an expired key
        // should NOT be rate-limited (the entry was removed by purge)
        ConnectivityTestService.ClearCooldowns();
        // Re-seed only the fresh entry to check it survived purge
        // We verify indirectly: if purge worked, "expired-user-1|99" is gone.
        // Seed it as expired again and set _lastPurgeTicks to 0 to re-trigger purge
        ConnectivityTestService.SeedCooldown("check-user", 99, freshTime);

        // The fresh entry user should be rate-limited (proving fresh entries survive)
        var freshExchange = CreateTestExchange();
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(97)).ReturnsAsync(freshExchange);

        // More direct verification: access the Cooldowns dictionary via reflection
        var cooldownsField = typeof(ConnectivityTestService)
            .GetField("Cooldowns", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cooldowns = (ConcurrentDictionary<string, DateTime>)cooldownsField.GetValue(null)!;

        // After RunTestAsync + purge: expired entries should be gone, fresh entry should remain
        // We need to re-run the purge scenario cleanly
        ConnectivityTestService.ClearCooldowns();
        ConnectivityTestService.SeedCooldown("expired-user-1", 99, expiredTime);
        ConnectivityTestService.SeedCooldown("expired-user-2", 98, expiredTime);
        ConnectivityTestService.SeedCooldown("fresh-user-1", 97, freshTime);
        lastPurgeField.SetValue(null, 0L);

        await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        // Verify: expired entries removed, fresh entry retained, plus the new entry from RunTestAsync
        cooldowns.ContainsKey("expired-user-1|99").Should().BeFalse("expired entry should be purged");
        cooldowns.ContainsKey("expired-user-2|98").Should().BeFalse("expired entry should be purged");
        cooldowns.ContainsKey("fresh-user-1|97").Should().BeTrue("fresh entry should survive purge");
        cooldowns.ContainsKey($"{TargetUserId}|{TestExchangeId}").Should().BeTrue("current test should add its own cooldown");
    }

    // ─── Dry-run and active-trade lock tests ─────────────────────────────────────

    private Mock<IExchangeConnector> CreateDryRunConnector(
        decimal balance = 100m,
        decimal markPrice = 3000m,
        bool balanceThrows = false,
        bool markPriceThrows = false,
        bool fundingRatesThrows = false)
    {
        var mock = new Mock<IExchangeConnector>();

        if (balanceThrows)
        {
            mock.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Auth failure"));
        }
        else
        {
            mock.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(balance);
        }

        if (markPriceThrows)
        {
            mock.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Market data unavailable"));
        }
        else
        {
            mock.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(markPrice);
        }

        if (fundingRatesThrows)
        {
            mock.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Funding rate endpoint down"));
        }
        else
        {
            mock.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<FundingRateArb.Application.DTOs.FundingRateDto>());
        }

        _mockConnectorFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", null, null, "wallet-addr", "private-key", null, null))
            .ReturnsAsync(mock.Object);

        return mock;
    }

    [Fact]
    public async Task DryRunMode_DoesNotPlaceRealOrders()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        var mockConnector = CreateDryRunConnector();

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeTrue();
        result.Mode.Should().Be("DryRun");

        // Read-only methods should each be called once
        mockConnector.Verify(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockConnector.Verify(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()), Times.Once);
        mockConnector.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Trading methods must never be called in dry-run mode
        mockConnector.Verify(
            c => c.PlaceMarketOrderAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mockConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReadOnlyCheck_ValidatesApiKey_AllSucceed_ReturnsSuccess()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateDryRunConnector(balance: 250m, markPrice: 3500m);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeTrue();
        result.Mode.Should().Be("DryRun");
    }

    [Fact]
    public async Task ReadOnlyCheck_ValidatesApiKey_BalanceFails_ReturnsFail()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateDryRunConnector(balanceThrows: true);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeFalse();
        result.Mode.Should().Be("DryRun");
        result.Error.Should().Contain("Balance check failed");
    }

    [Fact]
    public async Task ReadOnlyCheck_MarkPriceFails_ReturnsFail()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateDryRunConnector(markPriceThrows: true);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeFalse();
        result.Mode.Should().Be("DryRun");
        result.Error.Should().Contain("Mark price check failed");
    }

    [Fact]
    public async Task ReadOnlyCheck_FundingRateFails_ReturnsFail()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateDryRunConnector(fundingRatesThrows: true);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeFalse();
        result.Mode.Should().Be("DryRun");
        result.Error.Should().Contain("Funding rate check failed");
    }

    [Fact]
    public async Task DryRun_ZeroBalance_LogsWarning()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        CreateDryRunConnector(balance: 0m);

        var logMessages = new List<string>();
        _mockDashboardClient
            .Setup(d => d.ReceiveConnectivityLog(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((_, msg) => logMessages.Add(msg))
            .Returns(Task.CompletedTask);

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeTrue("a zero balance is not a failure");
        logMessages.Should().Contain(msg => msg.Contains("WARNING") && msg.Contains("quote asset"),
            "zero balance should produce a warning about the expected quote asset");
    }

    [Fact]
    public async Task ConcurrentLock_PreventsOverlappingTests()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);

        // Mock an open position for the same (userId, exchangeId) pair
        var openPosition = new ArbitragePosition
        {
            Id = 1,
            UserId = TargetUserId,
            LongExchangeId = TestExchangeId,
            ShortExchangeId = 2,
            Status = PositionStatus.Open
        };
        _mockPositionRepo
            .Setup(r => r.GetOpenByUserAsync(TargetUserId))
            .ReturnsAsync(new List<ArbitragePosition> { openPosition });

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: true);

        result.Success.Should().BeFalse();
        result.Error!.ToLowerInvariant().Should().Contain("open position");

        // No connector methods should be called
        _mockConnectorFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task RealTradeMode_PlacesRealOrders_AndReturnsLiveTradeMode()
    {
        var exchange = CreateTestExchange();
        var credential = CreateTestCredential();
        SetupExchangeAndCredential(exchange, credential);
        var mockConnector = CreateMockConnector();

        var result = await _sut.RunTestAsync(AdminUserId, TargetUserId, TestExchangeId, dryRun: false);

        result.Mode.Should().Be("LiveTrade");

        // PlaceMarketOrderAsync IS called in live-trade mode
        mockConnector.Verify(
            c => c.PlaceMarketOrderAsync("ETH", Side.Long, 10m, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
