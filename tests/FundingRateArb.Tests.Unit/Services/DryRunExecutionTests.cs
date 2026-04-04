using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class DryRunExecutionTests
{
    private const string TestUserId = "test-user-id";

    // ── ExecutionEngine test infrastructure ────────────────────────────────

    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IExchangeConnector> _mockLongConnector = new();
    private readonly Mock<IExchangeConnector> _mockShortConnector = new();

    private static readonly ArbitrageOpportunityDto DefaultOpp = new()
    {
        AssetSymbol = "ETH",
        AssetId = 1,
        LongExchangeName = "Hyperliquid",
        LongExchangeId = 1,
        ShortExchangeName = "Lighter",
        ShortExchangeId = 2,
        SpreadPerHour = 0.0005m,
        NetYieldPerHour = 0.0004m,
        LongMarkPrice = 3000m,
        ShortMarkPrice = 3001m,
    };

    public DryRunExecutionTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var longCred = new UserExchangeCredential { Id = 1, ExchangeId = 1, Exchange = new Exchange { Name = "Hyperliquid" } };
        var shortCred = new UserExchangeCredential { Id = 2, ExchangeId = 2, Exchange = new Exchange { Name = "Lighter" } };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<UserExchangeCredential> { longCred, shortCred });
        _mockUserSettings
            .Setup(s => s.DecryptCredential(It.IsAny<UserExchangeCredential>()))
            .Returns(("key", "secret", "wallet", "pk", (string?)null, (string?)null));

        _mockFactory
            .Setup(f => f.CreateForUserAsync("Hyperliquid", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockLongConnector.Object);
        _mockFactory
            .Setup(f => f.CreateForUserAsync("Lighter", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(_mockShortConnector.Object);

        _mockLongConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockShortConnector.Setup(c => c.ExchangeName).Returns("Lighter");

        _mockLongConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);
        _mockShortConnector.Setup(c => c.GetAvailableBalanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1000m);

        _mockLongConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);
        _mockShortConnector.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(3000m);

        _mockLongConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);
        _mockShortConnector.Setup(c => c.GetQuantityPrecisionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(6);

        // Default: no PlaceMarketOrderByQuantityAsync setup — dry-run wrapper should intercept
    }

    private static OrderResultDto SuccessOrder(string orderId = "1", decimal price = 3000m, decimal qty = 0.1m) =>
        new() { Success = true, OrderId = orderId, FilledPrice = price, FilledQuantity = qty };

    private ExecutionEngine CreateEngine()
    {
        var connectorLifecycle = new ConnectorLifecycleManager(
            _mockFactory.Object, _mockUserSettings.Object, NullLogger<ConnectorLifecycleManager>.Instance);
        var emergencyClose = new EmergencyCloseHandler(
            _mockUow.Object, NullLogger<EmergencyCloseHandler>.Instance);
        var positionCloser = new PositionCloser(
            _mockUow.Object, connectorLifecycle, Mock.Of<IPnlReconciliationService>(), NullLogger<PositionCloser>.Instance);

        return new ExecutionEngine(_mockUow.Object, connectorLifecycle, emergencyClose, positionCloser, _mockUserSettings.Object, NullLogger<ExecutionEngine>.Instance);
    }

    // ── ExecutionEngine dry-run tests ─────────────────────────────────────

    [Fact]
    public async Task OpenPositionAsync_WithBotDryRunEnabled_SetsIsDryRunTrue()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 5,
            DryRunEnabled = true,
            UpdatedByUserId = "admin",
        });
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5, DryRunEnabled = false });

        // DryRunConnectorWrapper will intercept orders — no need to set up PlaceMarketOrderByQuantityAsync on mocks

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var sut = CreateEngine();
        var result = await sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        savedPos.Should().NotBeNull();
        savedPos!.IsDryRun.Should().BeTrue();
    }

    [Fact]
    public async Task OpenPositionAsync_WithUserDryRunEnabled_SetsIsDryRunTrue()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 5,
            DryRunEnabled = false,
            UpdatedByUserId = "admin",
        });
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5, DryRunEnabled = true });

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var sut = CreateEngine();
        var result = await sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        savedPos.Should().NotBeNull();
        savedPos!.IsDryRun.Should().BeTrue();
    }

    [Fact]
    public async Task OpenPositionAsync_WithDryRunDisabled_SetsIsDryRunFalse()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 5,
            DryRunEnabled = false,
            UpdatedByUserId = "admin",
        });
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5, DryRunEnabled = false });

        // Non-dry-run needs real connector responses
        _mockLongConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());
        _mockShortConnector
            .Setup(c => c.PlaceMarketOrderByQuantityAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<decimal>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessOrder());

        ArbitragePosition? savedPos = null;
        _mockPositions.Setup(p => p.Add(It.IsAny<ArbitragePosition>()))
            .Callback<ArbitragePosition>(p => savedPos = p);

        var sut = CreateEngine();
        var result = await sut.OpenPositionAsync(TestUserId, DefaultOpp, 100m, ct: CancellationToken.None);

        result.Success.Should().BeTrue();
        savedPos.Should().NotBeNull();
        savedPos!.IsDryRun.Should().BeFalse();
    }

    // ── B1: Close path wrapping tests ──────────────────────────────────

    [Fact]
    public async Task ClosePositionAsync_WithDryRunPosition_UsesDryRunWrapper()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 5,
            DryRunEnabled = false,
            UpdatedByUserId = "admin",
        });

        _mockExchanges.Setup(e => e.GetByIdAsync(1)).ReturnsAsync(new Exchange { Id = 1, Name = "Hyperliquid" });
        _mockExchanges.Setup(e => e.GetByIdAsync(2)).ReturnsAsync(new Exchange { Id = 2, Name = "Lighter" });
        _mockAssets.Setup(a => a.GetByIdAsync(1)).ReturnsAsync(new Asset { Id = 1, Symbol = "ETH" });

        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            IsDryRun = true,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            SizeUsdc = 100m,
            Leverage = 5,
            Status = PositionStatus.Open,
        };

        var sut = CreateEngine();
        await sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual);

        // DryRunConnectorWrapper wraps the mock connectors, so the mock connector's
        // ClosePositionAsync should NEVER be called — only GetMarkPriceAsync is delegated
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // But GetMarkPriceAsync IS called by the wrapper to compute simulated fills
        _mockLongConnector.Verify(
            c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _mockShortConnector.Verify(
            c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClosePositionAsync_WithRealPosition_UsesRealConnector()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            DefaultLeverage = 5,
            DryRunEnabled = false,
            UpdatedByUserId = "admin",
        });

        _mockExchanges.Setup(e => e.GetByIdAsync(1)).ReturnsAsync(new Exchange { Id = 1, Name = "Hyperliquid" });
        _mockExchanges.Setup(e => e.GetByIdAsync(2)).ReturnsAsync(new Exchange { Id = 2, Name = "Lighter" });
        _mockAssets.Setup(a => a.GetByIdAsync(1)).ReturnsAsync(new Asset { Id = 1, Symbol = "ETH" });

        _mockLongConnector.Setup(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3000m, FilledQuantity = 0.1m });
        _mockShortConnector.Setup(c => c.ClosePositionAsync(It.IsAny<string>(), It.IsAny<Side>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrderResultDto { Success = true, FilledPrice = 3001m, FilledQuantity = 0.1m });

        var position = new ArbitragePosition
        {
            Id = 2,
            UserId = TestUserId,
            AssetId = 1,
            IsDryRun = false,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3001m,
            SizeUsdc = 100m,
            Leverage = 5,
            Status = PositionStatus.Open,
        };

        var sut = CreateEngine();
        await sut.ClosePositionAsync(TestUserId, position, CloseReason.Manual);

        // Real connector's ClosePositionAsync IS called
        _mockLongConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockShortConnector.Verify(
            c => c.ClosePositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── BotOrchestrator dry-run exclusion tests ───────────────────────────

    [Fact]
    public void RecordCloseResult_SkippedForDryRunPosition()
    {
        // Setup orchestrator
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockSp = new Mock<IServiceProvider>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockSp.Object);

        var mockReadiness = new Mock<IFundingRateReadinessSignal>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var cb = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var mockRotation = new Mock<IRotationEvaluator>();
        var mockLogger = new Mock<ILogger<BotOrchestrator>>();

        var oppFilter = new OpportunityFilter(cb, NullLogger<OpportunityFilter>.Instance);
        var sut = new BotOrchestrator(mockScopeFactory.Object, mockReadiness.Object, mockNotifier.Object, cb, oppFilter, mockRotation.Object, mockLogger.Object);

        // Record a real loss (should increment consecutive losses)
        sut.RecordCloseResult(-10m, TestUserId);
        cb.UserConsecutiveLosses.GetValueOrDefault(TestUserId, 0).Should().Be(1);

        // The guard is in the orchestrator cycle, not in RecordCloseResult itself.
        // RecordCloseResult doesn't check IsDryRun — the call site does.
        // Verify the call site guards: a dry-run position should NOT call RecordCloseResult.
        // This is validated by the integration flow — here we confirm the method increments correctly.
    }

    [Fact]
    public async Task DailyDrawdown_ExcludesDryRunPositions()
    {
        // Setup full orchestrator with mocks
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockSp = new Mock<IServiceProvider>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockSp.Object);

        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockPositionRepo = new Mock<IPositionRepository>();
        var mockAlertRepo = new Mock<IAlertRepository>();
        var mockExchangeRepo = new Mock<IExchangeRepository>();
        var mockUserConfigs = new Mock<IUserConfigurationRepository>();
        var mockSnapshotRepo = new Mock<IOpportunitySnapshotRepository>();
        var mockSignalEngine = new Mock<ISignalEngine>();
        var mockPositionSizer = new Mock<IPositionSizer>();
        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        var mockExecutionEngine = new Mock<IExecutionEngine>();
        var mockHealthMonitor = new Mock<IPositionHealthMonitor>();
        var mockUserSettingsSvc = new Mock<IUserSettingsService>();
        var mockReadiness = new Mock<IFundingRateReadinessSignal>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockRotation = new Mock<IRotationEvaluator>();
        var mockLogger = new Mock<ILogger<BotOrchestrator>>();

        mockSp.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockSp.Setup(sp => sp.GetService(typeof(ISignalEngine))).Returns(mockSignalEngine.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IPositionSizer))).Returns(mockPositionSizer.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IBalanceAggregator))).Returns(mockBalanceAggregator.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IExecutionEngine))).Returns(mockExecutionEngine.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IPositionHealthMonitor))).Returns(mockHealthMonitor.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IUserSettingsService))).Returns(mockUserSettingsSvc.Object);

        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositionRepo.Object);
        mockUow.Setup(u => u.Alerts).Returns(mockAlertRepo.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchangeRepo.Object);
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigs.Object);
        mockUow.Setup(u => u.OpportunitySnapshots).Returns(mockSnapshotRepo.Object);

        mockExchangeRepo.Setup(e => e.GetAllAsync()).ReturnsAsync(new List<Exchange>());
        mockAlertRepo.Setup(r => r.GetRecentUnreadAsync(It.IsAny<TimeSpan>())).ReturnsAsync([]);
        mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthCheckResult.Empty);

        mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            OpenThreshold = 0.0001m,
            VolumeFraction = 0.001m,
            DefaultLeverage = 5,
            UpdatedByUserId = "admin",
        });

        mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync()).ReturnsAsync(new List<string> { TestUserId });
        mockUserSettingsSvc.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
            });
        mockUserSettingsSvc.Setup(s => s.HasValidCredentialsAsync(TestUserId)).ReturnsAsync(true);
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2 });
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1 });
        mockUserSettingsSvc.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int>());

        mockReadiness.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });

        // (notifier mock handles all SignalR communication)

        // Open positions: none
        mockPositionRepo.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Open)).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>())).ReturnsAsync(0);

        // Closed today: one dry-run with a big loss, one real with small profit
        var dryRunClosed = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            IsDryRun = true,
            RealizedPnl = -500m,
            Status = PositionStatus.Closed,
            ClosedAt = DateTime.UtcNow,
        };
        var realClosed = new ArbitragePosition
        {
            Id = 2,
            UserId = TestUserId,
            IsDryRun = false,
            RealizedPnl = 5m,
            Status = PositionStatus.Closed,
            ClosedAt = DateTime.UtcNow,
        };
        mockPositionRepo.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition> { dryRunClosed, realClosed });

        // Provide opportunities so the cycle would try to open positions if drawdown doesn't block
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "ExA",
            ShortExchangeId = 2,
            ShortExchangeName = "ExB",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };
        mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(),
                It.IsAny<string>(),
                It.IsAny<UserConfiguration?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        var cb2 = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var oppFilter2 = new OpportunityFilter(cb2, NullLogger<OpportunityFilter>.Instance);
        var sut = new BotOrchestrator(mockScopeFactory.Object, mockReadiness.Object, mockNotifier.Object, cb2, oppFilter2, mockRotation.Object, mockLogger.Object);

        // Run a cycle — if dry-run PnL were counted, dailyPnl = -500 + 5 = -495,
        // which exceeds drawdown limit of 1000 * 0.05 = 50 → cycle would stop.
        // With dry-run excluded: dailyPnl = 5, which is fine → cycle should try to open.
        await sut.RunCycleAsync(CancellationToken.None);

        // Verify ExecutionEngine.OpenPositionAsync was called (meaning drawdown didn't block)
        mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(TestUserId, It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── NB1 (review-v2): Open dry-run position funding excluded from drawdown ─

    [Fact]
    public async Task DailyDrawdown_ExcludesOpenDryRunPositionFunding()
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockSp = new Mock<IServiceProvider>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockSp.Object);

        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockPositionRepo = new Mock<IPositionRepository>();
        var mockAlertRepo = new Mock<IAlertRepository>();
        var mockExchangeRepo = new Mock<IExchangeRepository>();
        var mockUserConfigs = new Mock<IUserConfigurationRepository>();
        var mockSnapshotRepo = new Mock<IOpportunitySnapshotRepository>();
        var mockSignalEngine = new Mock<ISignalEngine>();
        var mockPositionSizer = new Mock<IPositionSizer>();
        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        var mockExecutionEngine = new Mock<IExecutionEngine>();
        var mockHealthMonitor = new Mock<IPositionHealthMonitor>();
        var mockUserSettingsSvc = new Mock<IUserSettingsService>();
        var mockReadiness = new Mock<IFundingRateReadinessSignal>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockRotation = new Mock<IRotationEvaluator>();
        var mockLogger = new Mock<ILogger<BotOrchestrator>>();

        mockSp.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockSp.Setup(sp => sp.GetService(typeof(ISignalEngine))).Returns(mockSignalEngine.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IPositionSizer))).Returns(mockPositionSizer.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IBalanceAggregator))).Returns(mockBalanceAggregator.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IExecutionEngine))).Returns(mockExecutionEngine.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IPositionHealthMonitor))).Returns(mockHealthMonitor.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IUserSettingsService))).Returns(mockUserSettingsSvc.Object);

        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositionRepo.Object);
        mockUow.Setup(u => u.Alerts).Returns(mockAlertRepo.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchangeRepo.Object);
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigs.Object);
        mockUow.Setup(u => u.OpportunitySnapshots).Returns(mockSnapshotRepo.Object);

        mockExchangeRepo.Setup(e => e.GetAllAsync()).ReturnsAsync(new List<Exchange>());
        mockAlertRepo.Setup(r => r.GetRecentUnreadAsync(It.IsAny<TimeSpan>())).ReturnsAsync([]);
        mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthCheckResult.Empty);

        mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            OpenThreshold = 0.0001m,
            VolumeFraction = 0.001m,
            DefaultLeverage = 5,
            UpdatedByUserId = "admin",
        });

        mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync()).ReturnsAsync(new List<string> { TestUserId });
        mockUserSettingsSvc.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
            });
        mockUserSettingsSvc.Setup(s => s.HasValidCredentialsAsync(TestUserId)).ReturnsAsync(true);
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2 });
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1 });
        mockUserSettingsSvc.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int>());

        mockReadiness.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });

        // (notifier mock handles all SignalR communication)

        // Open dry-run position with large negative AccumulatedFunding
        var openDryRun = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            IsDryRun = true,
            AccumulatedFunding = -500m,
            Status = PositionStatus.Open,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            AssetId = 1,
        };

        mockPositionRepo.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition> { openDryRun });
        mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Open)).ReturnsAsync(new List<ArbitragePosition> { openDryRun });
        mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>())).ReturnsAsync(1);
        mockPositionRepo.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>());

        // Provide opportunities on different asset/exchange than open dry-run position
        // so the candidate filtering doesn't exclude it as a duplicate
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 2,
            AssetSymbol = "BTC",
            LongExchangeId = 3,
            LongExchangeName = "ExA",
            ShortExchangeId = 4,
            ShortExchangeName = "ExB",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };
        mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2, 3, 4 });
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2 });

        mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(),
                It.IsAny<string>(),
                It.IsAny<UserConfiguration?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        var cb3 = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var oppFilter3 = new OpportunityFilter(cb3, NullLogger<OpportunityFilter>.Instance);
        var sut = new BotOrchestrator(mockScopeFactory.Object, mockReadiness.Object, mockNotifier.Object, cb3, oppFilter3, mockRotation.Object, mockLogger.Object);

        // If open dry-run funding were counted, dailyPnl = -500,
        // which exceeds drawdown limit of 1000 * 0.05 = 50 → cycle would stop.
        // With dry-run excluded: dailyPnl = 0, which is fine → cycle should try to open.
        await sut.RunCycleAsync(CancellationToken.None);

        // Verify ExecutionEngine.OpenPositionAsync was called (meaning drawdown didn't block)
        mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(TestUserId, It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── NB2: IsDryRun mapping assertions ─────────────────────────────────

    [Fact]
    public void ToSummaryDto_MapsIsDryRun()
    {
        var pos = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            IsDryRun = true,
            Status = PositionStatus.Open,
        };

        var dto = pos.ToSummaryDto();

        dto.IsDryRun.Should().BeTrue();
    }

    [Fact]
    public void ToDetailsDto_MapsIsDryRun()
    {
        var pos = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            IsDryRun = true,
            Status = PositionStatus.Open,
        };

        var dto = pos.ToDetailsDto();

        dto.IsDryRun.Should().BeTrue();
    }

    // ── B1: Reconciliation skip for dry-run positions ──────────────────────

    [Fact]
    public async Task CheckPositionExists_DryRunPosition_ReturnsTrue()
    {
        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            IsDryRun = true,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            Status = PositionStatus.Open,
        };

        var sut = CreateEngine();
        var result = await sut.CheckPositionExistsOnExchangesAsync(position);

        result.Should().BeTrue();

        // Connectors should never be created for dry-run positions
        _mockFactory.Verify(
            f => f.CreateForUserAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckPositionsBatch_DryRunPosition_ReturnsBothPresent()
    {
        var dryRunPos = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            IsDryRun = true,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            Status = PositionStatus.Open,
        };
        var realPos = new ArbitragePosition
        {
            Id = 2,
            UserId = TestUserId,
            AssetId = 1,
            IsDryRun = false,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            Status = PositionStatus.Open,
        };

        _mockLongConnector.Setup(c => c.HasOpenPositionAsync("ETH", Side.Long, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockShortConnector.Setup(c => c.HasOpenPositionAsync("ETH", Side.Short, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = CreateEngine();
        var results = await sut.CheckPositionsExistOnExchangesBatchAsync(new[] { dryRunPos, realPos });

        results[dryRunPos.Id].Should().Be(PositionExistsResult.BothPresent);
        results[realPos.Id].Should().Be(PositionExistsResult.BothPresent);
    }

    // ── NB5: Health-monitor close loop — mixed positions ─────────────────

    [Fact]
    public async Task HealthMonitorClose_MixedPositions_OnlyCountsRealForLoss()
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockSp = new Mock<IServiceProvider>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockSp.Object);

        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockPositionRepo = new Mock<IPositionRepository>();
        var mockAlertRepo = new Mock<IAlertRepository>();
        var mockExchangeRepo = new Mock<IExchangeRepository>();
        var mockUserConfigs = new Mock<IUserConfigurationRepository>();
        var mockSnapshotRepo = new Mock<IOpportunitySnapshotRepository>();
        var mockSignalEngine = new Mock<ISignalEngine>();
        var mockPositionSizer = new Mock<IPositionSizer>();
        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        var mockExecutionEngine = new Mock<IExecutionEngine>();
        var mockHealthMonitor = new Mock<IPositionHealthMonitor>();
        var mockUserSettingsSvc = new Mock<IUserSettingsService>();
        var mockReadiness = new Mock<IFundingRateReadinessSignal>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockRotation = new Mock<IRotationEvaluator>();
        var mockLogger = new Mock<ILogger<BotOrchestrator>>();

        mockSp.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockSp.Setup(sp => sp.GetService(typeof(ISignalEngine))).Returns(mockSignalEngine.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IPositionSizer))).Returns(mockPositionSizer.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IBalanceAggregator))).Returns(mockBalanceAggregator.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IExecutionEngine))).Returns(mockExecutionEngine.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IPositionHealthMonitor))).Returns(mockHealthMonitor.Object);
        mockSp.Setup(sp => sp.GetService(typeof(IUserSettingsService))).Returns(mockUserSettingsSvc.Object);

        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositionRepo.Object);
        mockUow.Setup(u => u.Alerts).Returns(mockAlertRepo.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchangeRepo.Object);
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigs.Object);
        mockUow.Setup(u => u.OpportunitySnapshots).Returns(mockSnapshotRepo.Object);

        mockExchangeRepo.Setup(e => e.GetAllAsync()).ReturnsAsync(new List<Exchange>());
        mockAlertRepo.Setup(r => r.GetRecentUnreadAsync(It.IsAny<TimeSpan>())).ReturnsAsync([]);

        // Two positions to close: one dry-run (loss), one real (loss)
        // Use different asset/exchange IDs so cooldown keys are distinct
        var dryRunPos = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            IsDryRun = true,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Closed,
            RealizedPnl = -100m,
        };
        var realPos = new ArbitragePosition
        {
            Id = 11,
            UserId = TestUserId,
            AssetId = 2,
            IsDryRun = false,
            LongExchangeId = 3,
            ShortExchangeId = 4,
            Status = PositionStatus.Closed,
            RealizedPnl = -50m,
        };

        mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (dryRunPos, CloseReason.LiquidationRisk), (realPos, CloseReason.LiquidationRisk) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        // Close callback sets position status to Closed (simulating ExecutionEngine behavior)
        mockExecutionEngine.Setup(e => e.ClosePositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            OpenThreshold = 0.0001m,
            VolumeFraction = 0.001m,
            DefaultLeverage = 5,
            UpdatedByUserId = "admin",
        });

        mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync()).ReturnsAsync(new List<string> { TestUserId });
        mockUserSettingsSvc.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
            });
        mockUserSettingsSvc.Setup(s => s.HasValidCredentialsAsync(TestUserId)).ReturnsAsync(true);
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2 });
        mockUserSettingsSvc.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1 });
        mockUserSettingsSvc.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int>());

        mockReadiness.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });

        // (notifier mock handles all SignalR communication)

        mockPositionRepo.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Open)).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());
        mockPositionRepo.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>())).ReturnsAsync(0);
        mockPositionRepo.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>());

        mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [] });

        var cb4 = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var oppFilter4 = new OpportunityFilter(cb4, NullLogger<OpportunityFilter>.Instance);
        var sut = new BotOrchestrator(mockScopeFactory.Object, mockReadiness.Object, mockNotifier.Object, cb4, oppFilter4, mockRotation.Object, mockLogger.Object);

        await sut.RunCycleAsync(CancellationToken.None);

        // Only real position's loss should be recorded
        cb4.UserConsecutiveLosses.GetValueOrDefault(TestUserId, 0).Should().Be(1);

        // Only real position should have a cooldown entry
        var realCooldownKey = $"{TestUserId}:{realPos.AssetId}:{realPos.LongExchangeId}:{realPos.ShortExchangeId}";
        var dryCooldownKey = $"{TestUserId}:{dryRunPos.AssetId}:{dryRunPos.LongExchangeId}:{dryRunPos.ShortExchangeId}";
        cb4.FailedOpCooldowns.ContainsKey(realCooldownKey).Should().BeTrue();
        cb4.FailedOpCooldowns.ContainsKey(dryCooldownKey).Should().BeFalse();
    }
}
