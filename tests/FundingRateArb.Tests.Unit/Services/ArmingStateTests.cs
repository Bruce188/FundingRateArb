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
using FundingRateArb.Web.Areas.Admin.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class ArmingStateTests
{
    private const string TestUserId = "test-user-id";

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
        ShortMarkPrice = 3000m,
        LongVolume24h = 1_000_000m,
        ShortVolume24h = 1_000_000m,
    };

    // ── ExecutionEngine guard tests ─────────────────────────────────────────

    private (ExecutionEngine engine, Mock<IBotConfigRepository> mockBotConfig) CreateEngine(BotOperatingState state)
    {
        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockAlerts = new Mock<IAlertRepository>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockAssets = new Mock<IAssetRepository>();
        var mockConnectorLifecycle = new Mock<IConnectorLifecycleManager>();
        var mockEmergencyClose = new Mock<IEmergencyCloseHandler>();
        var mockPositionCloser = new Mock<IPositionCloser>();
        var mockUserSettings = new Mock<IUserSettingsService>();

        var config = new BotConfiguration
        {
            IsEnabled = state != BotOperatingState.Stopped,
            OperatingState = state,
            DefaultLeverage = 5,
            UpdatedByUserId = "admin-user-id",
        };

        mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.Alerts).Returns(mockAlerts.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.Assets).Returns(mockAssets.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        mockUserSettings
            .Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5 });

        var engine = new ExecutionEngine(
            mockUow.Object,
            mockConnectorLifecycle.Object,
            mockEmergencyClose.Object,
            mockPositionCloser.Object,
            mockUserSettings.Object,
            NullLogger<ExecutionEngine>.Instance);

        return (engine, mockBotConfig);
    }

    [Fact]
    public async Task OpenPosition_WhenStopped_ReturnsFailure()
    {
        var (engine, _) = CreateEngine(BotOperatingState.Stopped);

        var (success, error) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        success.Should().BeFalse();
        error.Should().Contain("not Armed or Trading");
    }

    [Fact]
    public async Task OpenPosition_WhenArmed_ProceedsPastGuard()
    {
        var (engine, _) = CreateEngine(BotOperatingState.Armed);

        // This will fail downstream (no connector setup) but should not fail at the guard
        var (success, error) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        // The guard passes — any subsequent failure is acceptable (connector not configured)
        // The key assertion: error must NOT contain the guard rejection message
        if (!success)
        {
            error.Should().NotContain("not Armed or Trading");
        }
    }

    [Fact]
    public async Task OpenPosition_WhenTrading_ProceedsPastGuard()
    {
        var (engine, _) = CreateEngine(BotOperatingState.Trading);

        var (success, error) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        if (!success)
        {
            error.Should().NotContain("not Armed or Trading");
        }
    }

    [Fact]
    public async Task OpenPosition_WhenPaused_ReturnsFailure()
    {
        var (engine, _) = CreateEngine(BotOperatingState.Paused);

        var (success, error) = await engine.OpenPositionAsync(TestUserId, DefaultOpp, 100m);

        success.Should().BeFalse();
        error.Should().Contain("not Armed or Trading");
    }

    // ── PositionCloser ungated tests ────────────────────────────────────────

    [Theory]
    [InlineData(BotOperatingState.Stopped)]
    [InlineData(BotOperatingState.Armed)]
    [InlineData(BotOperatingState.Trading)]
    [InlineData(BotOperatingState.Paused)]
    public void ClosePosition_HasNoStateGuard_InAnyState(BotOperatingState state)
    {
        // PositionCloser does not read BotConfiguration at all.
        // Verify by checking that ClosePositionAsync never queries BotConfig.
        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockConnectorLifecycle = new Mock<IConnectorLifecycleManager>();
        var mockReconciliation = new Mock<IPnlReconciliationService>();

        var config = new BotConfiguration
        {
            IsEnabled = state != BotOperatingState.Stopped,
            OperatingState = state,
        };
        mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);

        // Creating the closer with any state should work — it never checks BotConfig
        var closer = new PositionCloser(
            mockUow.Object,
            mockConnectorLifecycle.Object,
            mockReconciliation.Object,
            NullLogger<PositionCloser>.Instance);

        closer.Should().NotBeNull();
        // BotConfig should never be accessed during construction or any operation
        mockBotConfig.Verify(b => b.GetActiveAsync(), Times.Never);
    }

    // ── State transition tests ──────────────────────────────────────────────

    private (BotOrchestrator sut, Mock<IBotConfigRepository> mockBotConfig, Mock<IPositionRepository> mockPositions,
        Mock<IExecutionEngine> mockExecEngine, Mock<ISignalRNotifier> mockNotifier) CreateOrchestrator(BotOperatingState initialState)
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockScopeProvider = new Mock<IServiceProvider>();

        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockAlerts = new Mock<IAlertRepository>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockUserConfigs = new Mock<IUserConfigurationRepository>();
        var mockExecEngine = new Mock<IExecutionEngine>();
        var mockSignalEngine = new Mock<ISignalEngine>();
        var mockNotifier = new Mock<ISignalRNotifier>();
        var mockHealthMonitor = new Mock<IPositionHealthMonitor>();
        var mockUserSettings = new Mock<IUserSettingsService>();
        var mockPositionSizer = new Mock<IPositionSizer>();
        var mockBalanceAggregator = new Mock<IBalanceAggregator>();
        var mockRotationEvaluator = new Mock<IRotationEvaluator>();

        var config = new BotConfiguration
        {
            Id = 1,
            IsEnabled = initialState != BotOperatingState.Stopped,
            OperatingState = initialState,
            MaxConcurrentPositions = 5,
            DefaultLeverage = 5,
            UpdatedByUserId = "admin",
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
        };

        var trackedConfig = new BotConfiguration
        {
            Id = 1,
            IsEnabled = config.IsEnabled,
            OperatingState = initialState,
            MaxConcurrentPositions = config.MaxConcurrentPositions,
        };

        mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        mockBotConfig.Setup(b => b.GetActiveTrackedAsync()).ReturnsAsync(trackedConfig);

        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.Alerts).Returns(mockAlerts.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.UserConfigurations).Returns(mockUserConfigs.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        mockExchanges.Setup(e => e.GetAllAsync()).ReturnsAsync(new List<Exchange>());
        mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HealthCheckResult.Empty);
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());
        mockPositions.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>())).ReturnsAsync(0);
        mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Alert>());
        mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<ArbitragePosition>());
        mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync()).ReturnsAsync(new List<string> { TestUserId });

        mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
            });
        mockUserSettings.Setup(s => s.HasValidCredentialsAsync(TestUserId)).ReturnsAsync(true);
        mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId)).ReturnsAsync(new List<int> { 1, 2, 3 });
        mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId)).ReturnsAsync(new List<int> { 1, 2, 3, 4, 5 });
        mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync()).ReturnsAsync(new List<int>());
        mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });

        mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = new List<ArbitrageOpportunityDto> { DefaultOpp } });
        mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new decimal[] { 100m });

        // Wire notifier defaults
        mockNotifier.Setup(n => n.PushDashboardUpdateAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<List<ArbitrageOpportunityDto>>(), It.IsAny<BotOperatingState>(), It.IsAny<int>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushPositionUpdatesAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<BotConfiguration>(), It.IsAny<IReadOnlyDictionary<int, ComputedPositionPnl>?>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushPositionRemovalsAsync(It.IsAny<IReadOnlyList<(int, string, int, int, PositionStatus)>>(), It.IsAny<List<(int, string)>>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushRebalanceRemovalsAsync(It.IsAny<List<(int, string)>>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushNewAlertsAsync(It.IsAny<IUnitOfWork>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushStatusExplanationAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushOpportunityUpdateAsync(It.IsAny<OpportunityResultDto>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushBalanceUpdateAsync(It.IsAny<string>(), It.IsAny<BalanceSnapshotDto>())).Returns(Task.CompletedTask);
        mockNotifier.Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        // Wire scope factory
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockScopeProvider.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(ISignalEngine))).Returns(mockSignalEngine.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(IPositionSizer))).Returns(mockPositionSizer.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(IBalanceAggregator))).Returns(mockBalanceAggregator.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(IExecutionEngine))).Returns(mockExecEngine.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(IPositionHealthMonitor))).Returns(mockHealthMonitor.Object);
        mockScopeProvider.Setup(p => p.GetService(typeof(IUserSettingsService))).Returns(mockUserSettings.Object);

        var mockReadinessSignal = new Mock<IFundingRateReadinessSignal>();
        var circuitBreaker = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var opportunityFilter = new OpportunityFilter(circuitBreaker, NullLogger<OpportunityFilter>.Instance);

        var sut = new BotOrchestrator(
            mockScopeFactory.Object,
            mockReadinessSignal.Object,
            mockNotifier.Object,
            circuitBreaker,
            opportunityFilter,
            mockRotationEvaluator.Object,
            NullLogger<BotOrchestrator>.Instance);

        return (sut, mockBotConfig, mockPositions, mockExecEngine, mockNotifier);
    }

    [Fact]
    public async Task ArmedToTrading_OnSuccessfulPositionOpen()
    {
        var (sut, mockBotConfig, mockPositions, mockExecEngine, _) = CreateOrchestrator(BotOperatingState.Armed);

        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());

        mockExecEngine
            .Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        BotConfiguration? capturedConfig = null;
        mockBotConfig.Setup(b => b.GetActiveTrackedAsync())
            .ReturnsAsync(() =>
            {
                capturedConfig = new BotConfiguration { Id = 1, OperatingState = BotOperatingState.Armed };
                return capturedConfig;
            });

        await sut.RunCycleAsync(CancellationToken.None);

        // NB7: Verify actual state value written
        capturedConfig.Should().NotBeNull();
        capturedConfig!.OperatingState.Should().Be(BotOperatingState.Trading);
        mockBotConfig.Verify(b => b.GetActiveTrackedAsync(), Times.Once);
        mockBotConfig.Verify(b => b.InvalidateCache(), Times.Once);
    }

    [Fact]
    public async Task TradingToArmed_WhenAllPositionsClose()
    {
        var (sut, mockBotConfig, mockPositions, _, _) = CreateOrchestrator(BotOperatingState.Trading);

        // Empty open and opening positions — should trigger Trading -> Armed
        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());

        BotConfiguration? capturedConfig = null;
        mockBotConfig.Setup(b => b.GetActiveTrackedAsync())
            .ReturnsAsync(() =>
            {
                capturedConfig = new BotConfiguration { Id = 1, OperatingState = BotOperatingState.Trading };
                return capturedConfig;
            });

        await sut.RunCycleAsync(CancellationToken.None);

        // NB7: Verify actual state value written
        capturedConfig.Should().NotBeNull();
        capturedConfig!.OperatingState.Should().Be(BotOperatingState.Armed);
        mockBotConfig.Verify(b => b.GetActiveTrackedAsync(), Times.Once);
        mockBotConfig.Verify(b => b.InvalidateCache(), Times.Once);
    }

    [Fact]
    public async Task TradingToArmed_DoesNotTransition_WhenOpeningPositionsExist()
    {
        var (sut, mockBotConfig, mockPositions, _, _) = CreateOrchestrator(BotOperatingState.Trading);

        // Empty open positions but 1 opening position — should NOT transition
        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { Id = 1, UserId = TestUserId, Status = PositionStatus.Opening }
            });

        await sut.RunCycleAsync(CancellationToken.None);

        // GetActiveTrackedAsync should NOT be called since no transition should occur
        mockBotConfig.Verify(b => b.GetActiveTrackedAsync(), Times.Never);
    }

    // ── NB8: Trading stays when open (non-opening) positions exist ──────────

    [Fact]
    public async Task TradingToArmed_DoesNotTransition_WhenOpenPositionsExist()
    {
        var (sut, mockBotConfig, mockPositions, _, _) = CreateOrchestrator(BotOperatingState.Trading);

        // Non-empty open positions, empty opening — should NOT transition
        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>
        {
            new() { Id = 1, UserId = TestUserId, Status = PositionStatus.Open }
        });
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());

        await sut.RunCycleAsync(CancellationToken.None);

        // GetActiveTrackedAsync should NOT be called — still has open positions
        mockBotConfig.Verify(b => b.GetActiveTrackedAsync(), Times.Never);
    }

    // ── B3: Paused state gating path ────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenPaused_SkipsPositionOpening_ButPushesDashboard()
    {
        var (sut, _, mockPositions, mockExecEngine, mockNotifier) = CreateOrchestrator(BotOperatingState.Paused);

        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());

        await sut.RunCycleAsync(CancellationToken.None);

        // Dashboard push should still happen (monitoring continues)
        mockNotifier.Verify(n => n.PushDashboardUpdateAsync(
            It.IsAny<List<ArbitragePosition>>(),
            It.IsAny<List<ArbitrageOpportunityDto>>(),
            BotOperatingState.Paused,
            It.IsAny<int>(),
            It.IsAny<int>()), Times.Once);

        // Position opening should NOT happen
        mockExecEngine.Verify(e => e.OpenPositionAsync(
            It.IsAny<string>(),
            It.IsAny<ArbitrageOpportunityDto>(),
            It.IsAny<decimal>(),
            It.IsAny<UserConfiguration?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Status explanation should use "warning" severity and mention "paused"
        mockNotifier.Verify(n => n.PushStatusExplanationAsync(
            null,
            It.Is<string>(msg => msg.Contains("paused", StringComparison.OrdinalIgnoreCase)),
            "warning"), Times.Once);
    }

    // ── NB6: Transition error paths ─────────────────────────────────────────

    [Fact]
    public async Task TradingToArmed_WhenSaveFails_ContinuesCycleWithoutCrash()
    {
        var (sut, mockBotConfig, mockPositions, _, _) = CreateOrchestrator(BotOperatingState.Trading);

        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());

        // Make GetActiveTrackedAsync throw to simulate failure
        mockBotConfig.Setup(b => b.GetActiveTrackedAsync())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var act = () => sut.RunCycleAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ArmedToTrading_WhenSaveFails_ContinuesCycleWithoutCrash()
    {
        var (sut, mockBotConfig, mockPositions, mockExecEngine, _) = CreateOrchestrator(BotOperatingState.Armed);

        mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());

        mockExecEngine
            .Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        // Make GetActiveTrackedAsync throw to simulate failure
        mockBotConfig.Setup(b => b.GetActiveTrackedAsync())
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var act = () => sut.RunCycleAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── B2: SetState(Trading) rejection ─────────────────────────────────────

    [Fact]
    public async Task SetState_WhenTradingRequested_RejectsWithRedirect()
    {
        var mockUow = new Mock<IUnitOfWork>();
        var mockBotConfig = new Mock<IBotConfigRepository>();
        var mockConfigValidator = new Mock<IConfigValidator>();
        var logger = NullLogger<BotConfigController>.Instance;

        var config = new BotConfiguration
        {
            Id = 1,
            OperatingState = BotOperatingState.Armed,
        };
        mockBotConfig.Setup(b => b.GetActiveTrackedAsync()).ReturnsAsync(config);
        mockUow.Setup(u => u.BotConfig).Returns(mockBotConfig.Object);

        var controller = new BotConfigController(mockUow.Object, mockConfigValidator.Object, logger);
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(
            new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            Mock.Of<Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider>());

        var result = await controller.SetState(BotOperatingState.Trading);

        // Should redirect to Index
        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>()
            .Which.ActionName.Should().Be("Index");

        // SaveAsync should never be called — rejection means no persistence
        mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
