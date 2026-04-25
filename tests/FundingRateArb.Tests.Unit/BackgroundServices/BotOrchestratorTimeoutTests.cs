using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

/// <summary>
/// Tests for BotOrchestrator OpenConfirm-timeout sweep behaviour:
/// - Both legs zero-fill at timeout → Status = Failed, no ConfirmOrRollbackAsync call, no fee writes.
/// - One leg filled at timeout → ConfirmOrRollbackAsync IS invoked (existing emergency-close path preserved).
/// </summary>
public class BotOrchestratorTimeoutTests
{
    private const string TestUserId = "timeout-test-user";

    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockScopeProvider = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IUserConfigurationRepository> _mockUserConfigs = new();
    private readonly Mock<IExecutionEngine> _mockExecEngine = new();
    private readonly Mock<IFundingRateReadinessSignal> _mockReadinessSignal = new();
    private readonly Mock<ISignalRNotifier> _mockNotifier = new();
    private readonly Mock<IRotationEvaluator> _mockRotationEvaluator = new();
    private readonly CircuitBreakerManager _circuitBreaker;
    private readonly OpportunityFilter _opportunityFilter;
    private readonly BotOrchestrator _sut;

    private static readonly BotConfiguration ActiveConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        OpenConfirmTimeoutSeconds = 30,
        UpdatedByUserId = "admin",
    };

    public BotOrchestratorTimeoutTests()
    {
        // Wire scope factory
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);

        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IExecutionEngine))).Returns(_mockExecEngine.Object);

        // Wire UoW
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Default bot config
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(ActiveConfig);

        // Default: one enabled user
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // Default: no pending confirm positions (overridden per test)
        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition>());

        // Default: ConfirmOrRollbackAsync is a no-op
        _mockExecEngine
            .Setup(e => e.ConfirmOrRollbackAsync(
                It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockReadinessSignal.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _circuitBreaker = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        _opportunityFilter = new OpportunityFilter(_circuitBreaker, NullLogger<OpportunityFilter>.Instance);

        _sut = new BotOrchestrator(
            _mockScopeFactory.Object,
            _mockReadinessSignal.Object,
            _mockNotifier.Object,
            _circuitBreaker,
            _opportunityFilter,
            _mockRotationEvaluator.Object,
            NullLogger<BotOrchestrator>.Instance);
    }

    // ── Both legs zero-fill ───────────────────────────────────────────────────

    /// <summary>
    /// When the OpenConfirm timeout fires and both LongFilledQuantity and ShortFilledQuantity
    /// are zero, the orchestrator must mark the position Failed without invoking
    /// ConfirmOrRollbackAsync (and therefore without writing any entry/exit fees).
    /// </summary>
    [Fact]
    public async Task RunBootSweep_BothLegsZeroFill_SetsFailed_DoesNotCallConfirmOrRollback()
    {
        // Arrange — Opening position with zero fill on both legs
        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = 0m,
            ShortFilledQuantity = 0m,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert — position promoted to Failed
        position.Status.Should().Be(PositionStatus.Failed,
            "a zero-fill timeout position has no real exposure and must be marked Failed, not rolled back via exchange calls");

        // Assert — ConfirmOrRollbackAsync never called (no exchange round-trip for zero-fill)
        _mockExecEngine.Verify(
            e => e.ConfirmOrRollbackAsync(
                It.IsAny<string>(), It.IsAny<ArbitragePosition>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "zero-fill positions must be short-circuited in the orchestrator; ConfirmOrRollbackAsync must not be called");
    }

    /// <summary>
    /// Zero-fill timeout positions must not have fee fields populated,
    /// because no capital was ever deployed on an exchange.
    /// </summary>
    [Fact]
    public async Task RunBootSweep_BothLegsZeroFill_DoesNotWriteFees()
    {
        // Arrange
        var position = new ArbitragePosition
        {
            Id = 2,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = 0m,
            ShortFilledQuantity = 0m,
            EntryFeesUsdc = 0m,
            ExitFeesUsdc = 0m,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert — no fee writes; fees remain zero (no phantom fees from a zero-fill position)
        position.EntryFeesUsdc.Should().Be(0m,
            "no fees were incurred on a zero-fill position — writing phantom fees would corrupt PnL");
        position.ExitFeesUsdc.Should().Be(0m,
            "no exit fees should be recorded when neither leg was ever filled");
    }

    /// <summary>
    /// A zero-fill timeout position must be persisted (SaveAsync called) so the
    /// Failed status is durable after the sweep.
    /// </summary>
    [Fact]
    public async Task RunBootSweep_BothLegsZeroFill_PersistsFailedStatus()
    {
        // Arrange
        var position = new ArbitragePosition
        {
            Id = 3,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = 0m,
            ShortFilledQuantity = 0m,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert — persistence occurred (UoW saved)
        _mockUow.Verify(
            u => u.SaveAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "the Failed status must be persisted to the database");
    }

    // ── One leg filled (emergency-close path must be preserved) ──────────────

    /// <summary>
    /// When the timeout fires and one leg HAS a non-zero fill, the orchestrator must
    /// delegate to ConfirmOrRollbackAsync so the existing naked-leg emergency-close
    /// safety net runs unchanged.
    /// </summary>
    [Fact]
    public async Task RunBootSweep_LongLegFilled_CallsConfirmOrRollback()
    {
        // Arrange — only the long leg has a fill
        var position = new ArbitragePosition
        {
            Id = 4,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = 1.5m,
            ShortFilledQuantity = 0m,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert — emergency-close path invoked
        _mockExecEngine.Verify(
            e => e.ConfirmOrRollbackAsync(
                TestUserId, position, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a position with a filled leg has real exchange exposure and must go through ConfirmOrRollbackAsync");
    }

    /// <summary>
    /// When the timeout fires and the short leg HAS a non-zero fill, the orchestrator must
    /// also delegate to ConfirmOrRollbackAsync.
    /// </summary>
    [Fact]
    public async Task RunBootSweep_ShortLegFilled_CallsConfirmOrRollback()
    {
        // Arrange — only the short leg has a fill
        var position = new ArbitragePosition
        {
            Id = 5,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = 0m,
            ShortFilledQuantity = 0.8m,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert — emergency-close path invoked
        _mockExecEngine.Verify(
            e => e.ConfirmOrRollbackAsync(
                TestUserId, position, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a position with a filled short leg must be routed through ConfirmOrRollbackAsync for emergency close");
    }

    /// <summary>
    /// When both legs are filled (normal confirm scenario), ConfirmOrRollbackAsync must
    /// still be called — the zero-fill gate must not affect non-zero-fill positions.
    /// </summary>
    [Fact]
    public async Task RunBootSweep_BothLegsFilled_CallsConfirmOrRollback()
    {
        // Arrange — both legs filled (open position waiting for exchange confirmation)
        var position = new ArbitragePosition
        {
            Id = 6,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = 1.0m,
            ShortFilledQuantity = 1.0m,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert — normal confirm-or-rollback path
        _mockExecEngine.Verify(
            e => e.ConfirmOrRollbackAsync(
                TestUserId, position, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "fully-filled positions must be processed normally by ConfirmOrRollbackAsync");
    }

    // ── Null fill quantities treated as zero ─────────────────────────────────

    /// <summary>
    /// Null LongFilledQuantity and ShortFilledQuantity must be treated the same as
    /// explicit zero — the position was never filled, so mark Failed without fee writes.
    /// </summary>
    [Fact]
    public async Task RunBootSweep_NullFilledQuantities_TreatedAsZeroFill_SetsFailed()
    {
        // Arrange — null fill quantities (position never reached exchange fill stage)
        var position = new ArbitragePosition
        {
            Id = 7,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            LongFilledQuantity = null,
            ShortFilledQuantity = null,
        };

        _mockPositions
            .Setup(p => p.GetPendingConfirmAsync(
                TestUserId, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<int>()))
            .ReturnsAsync(new List<ArbitragePosition> { position });

        // Act
        await _sut.RunBootSweepAsync(CancellationToken.None);

        // Assert
        position.Status.Should().Be(PositionStatus.Failed,
            "null fill quantities are semantically equivalent to zero fill — no exchange exposure exists");

        _mockExecEngine.Verify(
            e => e.ConfirmOrRollbackAsync(
                It.IsAny<string>(), It.IsAny<ArbitragePosition>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "null fill positions must be short-circuited just like explicit zero-fill positions");
    }
}
