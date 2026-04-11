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

public class BotOrchestratorTests
{
    private const string TestUserId = "test-user-id";

    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockScopeProvider = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IUserConfigurationRepository> _mockUserConfigs = new();
    private readonly Mock<ISignalEngine> _mockSignalEngine = new();
    private readonly Mock<IPositionSizer> _mockPositionSizer = new();
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator = new();
    private readonly Mock<IExecutionEngine> _mockExecEngine = new();
    private readonly Mock<IPositionHealthMonitor> _mockHealthMonitor = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IFundingRateReadinessSignal> _mockReadinessSignal = new();
    private readonly Mock<ISignalRNotifier> _mockNotifier = new();
    private readonly Mock<IRotationEvaluator> _mockRotationEvaluator = new();
    private readonly CircuitBreakerManager _circuitBreaker;
    private readonly OpportunityFilter _opportunityFilter;
    private readonly BotOrchestrator _sut;

    private static readonly BotConfiguration EnabledConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        MaxConcurrentPositions = 1,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        UpdatedByUserId = "admin-user-id",
        AllocationStrategy = Domain.Enums.AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        TotalCapitalUsdc = 1000m,
        MaxCapitalPerPosition = 0.5m,
        VolumeFraction = 0.001m,
    };

    private static readonly BotConfiguration DisabledConfig = new()
    {
        IsEnabled = false,
        OperatingState = BotOperatingState.Stopped,
    };

    private static readonly UserConfiguration EnabledUserConfig = new()
    {
        UserId = TestUserId,
        IsEnabled = true,
        MaxConcurrentPositions = 1,
        DefaultLeverage = 5,
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        TotalCapitalUsdc = 1000m,
        MaxCapitalPerPosition = 0.5m,
        OpenThreshold = 0.0001m,
        DailyDrawdownPausePct = 0.05m,
        ConsecutiveLossPause = 3,
    };

    public BotOrchestratorTests()
    {
        // Wire scope factory
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);

        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(ISignalEngine))).Returns(_mockSignalEngine.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPositionSizer))).Returns(_mockPositionSizer.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IBalanceAggregator))).Returns(_mockBalanceAggregator.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IExecutionEngine))).Returns(_mockExecEngine.Object);

        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPositionHealthMonitor))).Returns(_mockHealthMonitor.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IUserSettingsService))).Returns(_mockUserSettings.Object);

        // Wire UoW
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);

        // Default mock for exchange repository (used in circuit breaker DTO population)
        _mockExchanges.Setup(e => e.GetAllAsync())
            .ReturnsAsync(new List<Exchange>());

        // Default mock for health monitor — returns empty result
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthCheckResult.Empty);

        // Default mock for Opening status query (used in duplicate check)
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition>());

        // Default mock for needs-attention count query (used in dashboard KPI)
        _mockPositions.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(0);

        // M12: Default mock for GetRecentUnreadAsync (returns empty list)
        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Alert>());

        // H7: Default mock for GetClosedSinceAsync (returns empty list — no drawdown)
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>());

        // Default: no enabled users (tests that need users configure this explicitly)
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string>());

        // Default user settings mocks for tests that configure an enabled user
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(EnabledUserConfig);
        _mockUserSettings.Setup(s => s.HasValidCredentialsAsync(TestUserId))
            .ReturnsAsync(true);
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2, 3 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2, 3, 4, 5 });
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int>());

        // Wire notifier — default all methods to completed tasks
        _mockNotifier.Setup(n => n.PushDashboardUpdateAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<List<ArbitrageOpportunityDto>>(), It.IsAny<BotOperatingState>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<OpportunityResultDto?>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushPositionUpdatesAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<BotConfiguration>(), It.IsAny<IReadOnlyDictionary<int, ComputedPositionPnl>?>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushPositionRemovalsAsync(It.IsAny<IReadOnlyList<(int, string, int, int, PositionStatus)>>(), It.IsAny<List<(int, string)>>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushRebalanceRemovalsAsync(It.IsAny<List<(int, string)>>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushNewAlertsAsync(It.IsAny<IUnitOfWork>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushStatusExplanationAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushOpportunityUpdateAsync(It.IsAny<OpportunityResultDto>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushBalanceUpdateAsync(It.IsAny<string>(), It.IsAny<BalanceSnapshotDto>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

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

    /// <summary>Sets up mocks so RunCycleAsync finds one enabled user.</summary>
    private void SetupEnabledUser()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });
    }

    // ── Kill switch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenKillSwitchOff_StillRunsHealthMonitor()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Opportunities are always computed even when disabled
        _mockSignalEngine.Verify(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()), Times.Once);
        // H1: Health monitor ALWAYS runs — even when kill switch is off
        _mockHealthMonitor.Verify(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Health monitor called first ────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_CallsHealthMonitorFirst()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var callOrder = new List<string>();
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("health"))
            .ReturnsAsync(HealthCheckResult.Empty);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("signal"))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        callOrder.Should().ContainInOrder("health", "signal");
    }

    // ── Max positions reached ──────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenMaxPositionsReached_DoesNotOpenNew()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        // MaxConcurrentPositions = 1 (user config), already have 1 open for this user
        _mockPositions.Setup(p => p.GetOpenAsync())
            .ReturnsAsync([new ArbitragePosition { UserId = TestUserId, Status = PositionStatus.Open }]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Opportunities are always computed before the max-positions gate
        _mockSignalEngine.Verify(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()), Times.Once);
        // Execution engine must not be called — no new positions should be opened
        _mockExecEngine.Verify(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Opens at most one position per cycle ───────────────────────────────────

    [Fact]
    public async Task RunCycle_OpensAtMostOnePositionPerCycle()
    {
        SetupEnabledUser();
        // Concentrated strategy takes only 1 candidate
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.001m };
        var opp2 = new ArbitrageOpportunityDto { AssetId = 2, AssetSymbol = "BTC", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.0005m };

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Only 1 position opened even with 2 opportunities (Concentrated takes 1)
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Skips already-open position pairs ─────────────────────────────────────

    [Fact]
    public async Task RunCycle_SkipsAlreadyOpenPositions()
    {
        SetupEnabledUser();
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 2,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
            });

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 2, // allow more
        });

        // ETH Hyperliquid/Lighter already open for this user
        _mockPositions.Setup(p => p.GetOpenAsync())
            .ReturnsAsync([new ArbitragePosition { UserId = TestUserId, AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2 }]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,  // same pair
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Zero size -> skip ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenSizeIsZero_DoesNotOpenPosition()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.001m };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0m]);

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── H-BO1: Position updates go to per-user group, not Clients.All ─────────

    [Fact]
    public async Task RunCycle_PushesPositionUpdates_ViaNotifier()
    {
        var openPosition = new ArbitragePosition
        {
            Id = 1,
            UserId = "trader-user-id",
            Status = Domain.Enums.PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Lighter" },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([openPosition]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(
            n => n.PushPositionUpdatesAsync(It.Is<List<ArbitragePosition>>(l => l.Count == 1), It.IsAny<BotConfiguration>(), It.IsAny<IReadOnlyDictionary<int, ComputedPositionPnl>?>()),
            Times.Once);
    }

    // ── H-BO1: Dashboard aggregates go via notifier ───────────────────

    [Fact]
    public async Task RunCycle_PushesDashboardUpdate_ViaNotifier()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(
            n => n.PushDashboardUpdateAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<List<ArbitrageOpportunityDto>>(), It.IsAny<BotOperatingState>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<OpportunityResultDto?>()),
            Times.Once);
    }

    // ── H7: Position-open notification goes via notifier ──

    [Fact]
    public async Task RunCycle_OnPositionOpen_SendsNotification_ViaNotifier()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(
            n => n.PushNotificationAsync(TestUserId, It.Is<string>(m => m.Contains("ETH"))),
            Times.Once);
    }

    // ── H8: BotOrchestrator pushes opportunity updates via notifier ──

    [Fact]
    public async Task RunCycle_PushesOpportunityUpdate_ViaNotifier()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opportunities = new List<ArbitrageOpportunityDto>
        {
            new() { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.001m },
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = opportunities });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(
            n => n.PushOpportunityUpdateAsync(It.IsAny<OpportunityResultDto>()),
            Times.Once);
    }

    // ── M6: Position list is re-fetched AFTER health monitor closes positions ──

    [Fact]
    public async Task RunCycle_FetchesOpenPositions_AfterHealthMonitor_NotBefore()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var callOrder = new List<string>();

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("health"))
            .ReturnsAsync(HealthCheckResult.Empty);

        _mockPositions.Setup(p => p.GetOpenAsync())
            .Callback(() => callOrder.Add("GetOpen"))
            .ReturnsAsync([]);

        await _sut.RunCycleAsync(CancellationToken.None);

        // GetOpenAsync must be called AFTER the health monitor
        var healthIndex = callOrder.IndexOf("health");
        var getOpenIndex = callOrder.IndexOf("GetOpen");

        healthIndex.Should().BeGreaterThanOrEqualTo(0, "health monitor must run");
        getOpenIndex.Should().BeGreaterThan(healthIndex,
            "GetOpenAsync must be called after health monitor to avoid stale closed positions");
    }

    // ── M7: Alert push uses rolling cutoff, not fixed 2-minute window ─────────

    [Fact]
    public async Task RunCycle_CallsPushNewAlerts_EachCycle()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        // Two cycles
        await _sut.RunCycleAsync(CancellationToken.None);
        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(n => n.PushNewAlertsAsync(It.IsAny<IUnitOfWork>()), Times.Exactly(2),
            "PushNewAlertsAsync should be called once per cycle");
    }

    // ── M-BO2: SemaphoreSlim is disposed when orchestrator is disposed ─────────

    [Fact]
    public void BotOrchestrator_Dispose_DoesNotThrow()
    {
        var act = () => _sut.Dispose();
        act.Should().NotThrow();
    }

    // ── C3: Failed opportunity cooldown ─────────────────────────────────────────

    [Fact]
    public async Task RunCycle_AfterFailure_SuppressesRetryOnNextCycle()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        // Cycle 1: fails
        await _sut.RunCycleAsync(CancellationToken.None);
        // Cycle 2: same opportunity should be skipped due to cooldown
        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once, "Failed opportunity must be on cooldown and not retried on the next cycle");
    }

    [Fact]
    public async Task RunCycle_AfterCooldownExpires_RetriesOpportunity()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        // Cycle 1: fails — registers cooldown
        await _sut.RunCycleAsync(CancellationToken.None);

        // Manually expire the cooldown for testing (per-user key)
        var key = $"{TestUserId}:1_1_2";
        var entry = _circuitBreaker.FailedOpCooldowns[key];
        _circuitBreaker.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), entry.Failures);

        // Cycle 2: cooldown expired — should retry
        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "Opportunity must be retried after cooldown expires");
    }

    [Fact]
    public async Task RunCycle_SuccessfulOpen_ClearsCooldown()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // First call fails, second succeeds
        _mockExecEngine.SetupSequence(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"))
            .ReturnsAsync((true, (string?)null));

        // Cycle 1: fails
        await _sut.RunCycleAsync(CancellationToken.None);
        var key = $"{TestUserId}:1_1_2";
        _circuitBreaker.FailedOpCooldowns.ContainsKey(key).Should().BeTrue("cooldown must be registered after failure");

        // Expire cooldown so cycle 2 retries
        var entry = _circuitBreaker.FailedOpCooldowns[key];
        _circuitBreaker.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), entry.Failures);

        // Cycle 2: succeeds — clears cooldown
        await _sut.RunCycleAsync(CancellationToken.None);
        _circuitBreaker.FailedOpCooldowns.ContainsKey(key).Should().BeFalse("cooldown must be cleared after successful open");
    }

    [Fact]
    public async Task RunCycle_ExponentialBackoff_IncreasesWithConsecutiveFailures()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        var key = $"{TestUserId}:1_1_2";

        // Failure 1: cooldown = BaseCooldown (5 min)
        await _sut.RunCycleAsync(CancellationToken.None);
        var cooldown1 = _circuitBreaker.FailedOpCooldowns[key];
        cooldown1.Failures.Should().Be(1);

        // Expire and fail again
        _circuitBreaker.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), cooldown1.Failures);
        await _sut.RunCycleAsync(CancellationToken.None);
        var cooldown2 = _circuitBreaker.FailedOpCooldowns[key];
        cooldown2.Failures.Should().Be(2);

        // Expire and fail a third time
        _circuitBreaker.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), cooldown2.Failures);
        await _sut.RunCycleAsync(CancellationToken.None);
        var cooldown3 = _circuitBreaker.FailedOpCooldowns[key];
        cooldown3.Failures.Should().Be(3);

        // After 3 failures, cooldown should be longer than after 1 failure
        var duration1 = CircuitBreakerManager.BaseCooldown; // 5 min
        var duration3 = TimeSpan.FromTicks(
            Math.Min(CircuitBreakerManager.BaseCooldown.Ticks * (1L << 2), CircuitBreakerManager.MaxCooldown.Ticks)); // 20 min
        duration3.Should().BeGreaterThan(duration1, "exponential backoff must increase cooldown with consecutive failures");
    }

    // ── H1: Health monitor always runs ──────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenDisabled_StillCallsHealthMonitor()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockHealthMonitor.Verify(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()), Times.Once,
            "Health monitor must run even when bot is disabled to prevent stale/unliquidated positions");
    }

    // ── H6: Alerts are pushed via notifier ──────────────────────────────────────

    [Fact]
    public async Task RunCycle_CallsPushNewAlerts_ViaNotifier()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(n => n.PushNewAlertsAsync(It.IsAny<IUnitOfWork>()), Times.Once);
    }

    // ── H7: Daily drawdown circuit breaker ──────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenDailyDrawdownExceeded_DoesNotOpenPositions()
    {
        SetupEnabledUser();
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            DailyDrawdownPausePct = 0.05m,
            UpdatedByUserId = "admin-user-id",
            AllocationStrategy = AllocationStrategy.Concentrated,
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        // Closed positions today with -$60 realized loss for this user
        var closedPosition = new ArbitragePosition
        {
            UserId = TestUserId,
            RealizedPnl = -60m,
            Status = PositionStatus.Closed,
        };
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync([closedPosition]);

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Drawdown limit exceeded — no new positions should be opened");
    }

    [Fact]
    public async Task RunCycle_WhenDailyDrawdownWithinLimit_AllowsPositionOpens()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var closedPosition = new ArbitragePosition
        {
            UserId = TestUserId,
            RealizedPnl = -10m,
            Status = PositionStatus.Closed,
        };
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync([closedPosition]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Drawdown within limit — position open should proceed");
    }

    // ── M6: Consecutive loss tracking ───────────────────────────────────────────

    [Fact]
    public async Task RunCycle_AfterConsecutiveLosses_PausesPositionOpens()
    {
        SetupEnabledUser();
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                ConsecutiveLossPause = 2,
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 3,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
            });

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        // Pre-set per-user consecutive losses at the pause threshold
        _circuitBreaker.UserConsecutiveLosses[TestUserId] = 2;

        var opp = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, LongExchangeName = "ExA", ShortExchangeName = "ExB", NetYieldPerHour = 0.001m };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Position opens must be paused when consecutive loss limit is reached");
    }

    [Fact]
    public async Task RunCycle_SuccessfulOpen_DoesNotResetConsecutiveLosses()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        // Pre-set per-user consecutive losses (below pause threshold of 3)
        _circuitBreaker.UserConsecutiveLosses[TestUserId] = 2;

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        _circuitBreaker.UserConsecutiveLosses[TestUserId].Should().Be(2, "consecutive losses are only reset by RecordCloseResult, not by a successful open");
    }

    [Fact]
    public async Task RunCycle_ConsecutiveLossPausePreventsNextCycle()
    {
        SetupEnabledUser();
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                ConsecutiveLossPause = 2,
                AllocationStrategy = AllocationStrategy.Concentrated,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
            });

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        _circuitBreaker.UserConsecutiveLosses[TestUserId] = 2;

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Position opens must be paused when consecutive loss limit is reached");
    }

    // ── D2: Consecutive loss tracking via RecordCloseResult ──────────────────

    [Fact]
    public void RecordCloseResult_NegativePnl_IncrementsCounter()
    {
        _sut.RecordCloseResult(-5m, TestUserId);
        _sut.RecordCloseResult(-5m, TestUserId);
        _sut.RecordCloseResult(-5m, TestUserId);

        _circuitBreaker.UserConsecutiveLosses[TestUserId].Should().Be(3);
    }

    [Fact]
    public void RecordCloseResult_PositivePnl_ResetsCounter()
    {
        _sut.RecordCloseResult(-1m, TestUserId);
        _sut.RecordCloseResult(-1m, TestUserId);
        _sut.RecordCloseResult(1m, TestUserId);

        _circuitBreaker.UserConsecutiveLosses[TestUserId].Should().Be(0);
    }

    [Fact]
    public async Task OpenFails_DoesNotIncrementConsecutiveLosses()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "API timeout"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _circuitBreaker.UserConsecutiveLosses.GetValueOrDefault(TestUserId, 0).Should().Be(0,
            "open failures do not count as realized losses — only RecordCloseResult increments the counter");
    }

    // ── D5: Opening status duplicate check ──────────────────────────────────────

    [Fact]
    public async Task RunCycle_DuplicateCheck_IncludesOpeningStatus()
    {
        SetupEnabledUser();
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
            });
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        // Existing Opening position for asset 1 on exchanges 1/2 owned by test user
        var openingPos = new ArbitragePosition
        {
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Opening,
        };
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync([openingPos]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never, "Opening-status position should block duplicate opportunity");
    }

    // ── D5: RecordCloseResult only on successful close ──────────────────────────

    [Fact]
    public async Task RunCycle_RecordCloseResult_OnlyOnSuccessfulClose()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        // Health monitor returns a position to close
        var pos = new ArbitragePosition
        {
            Id = 10,
            UserId = "admin-user-id",
            Status = PositionStatus.Open,
            RealizedPnl = null, // PnL not set
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.SpreadCollapsed) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        // After ClosePositionAsync, position stays in Closing (partial fill)
        _mockExecEngine.Setup(e => e.ClosePositionAsync(It.IsAny<string>(), pos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()))
            .Callback(() => { pos.Status = PositionStatus.Closing; })
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        // RecordCloseResult should NOT have been called since pos is not Closed
        _circuitBreaker.UserConsecutiveLosses.GetValueOrDefault("admin-user-id", 0).Should().Be(0,
            "RecordCloseResult should only be called when position.Status == Closed");
    }

    // ── D5: Balance exhaustion breaks loop ──────────────────────────────────────

    [Fact]
    public async Task RunCycle_BalanceExhaustion_BreaksLoop()
    {
        SetupEnabledUser();
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 3,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
            });

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, LongExchangeName = "ExA", ShortExchangeName = "ExB", NetYieldPerHour = 0.001m };
        var opp2 = new ArbitrageOpportunityDto { AssetId = 2, AssetSymbol = "BTC", LongExchangeId = 1, ShortExchangeId = 3, LongExchangeName = "ExA", ShortExchangeName = "ExC", NetYieldPerHour = 0.001m };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m, 100m]);

        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin for order"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once, "Balance exhaustion should break the position-opening loop");
    }

    // ── D5: First cycle catches startup alerts ──────────────────────────────────

    [Fact]
    public async Task RunCycle_FirstCycle_CallsPushNewAlerts()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockNotifier.Verify(n => n.PushNewAlertsAsync(It.IsAny<IUnitOfWork>()), Times.Once,
            "PushNewAlertsAsync must be called each cycle to deliver alerts");
    }

    // ── Data-only exchange filtering ─────────────────────────────────────────

    [Theory]
    [InlineData(4, 2)]  // data-only on Long side
    [InlineData(2, 4)]  // data-only on Short side
    public async Task ExecuteUserCycle_OpportunityWithDataOnlyExchange_Skipped(int longExchangeId, int shortExchangeId)
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        // CoinGlass (id=4) is data-only
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int> { 4 });

        // Opportunity pairs a tradeable exchange with a data-only one
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = longExchangeId,
            ShortExchangeId = shortExchangeId,
            NetYieldPerHour = 0.01m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        // Execution engine should NOT be called — data-only exchange opportunities are skipped
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Ghost Position Removal Tests ─────────────────────────────────────────

    [Fact]
    public async Task ReapedPositions_PushRemovalEvent()
    {
        // Arrange — health monitor returns reaped position IDs
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>
        {
            (42, TestUserId, 1, 2, PositionStatus.Closing),
            (99, TestUserId, 1, 2, PositionStatus.Closing),
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                reapedPositions,
                new Dictionary<int, ComputedPositionPnl>()));
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — PushPositionRemovalsAsync called with reaped positions
        _mockNotifier.Verify(
            n => n.PushPositionRemovalsAsync(
                It.Is<IReadOnlyList<(int, string, int, int, PositionStatus)>>(l => l.Count == 2),
                It.IsAny<List<(int, string)>>()),
            Times.Once);
    }

    [Fact]
    public async Task ClosedPositions_PushRemovalEvent()
    {
        // Arrange — health monitor returns a position to close
        var pos = new ArbitragePosition
        {
            Id = 55,
            UserId = TestUserId,
            Status = PositionStatus.Open,
            RealizedPnl = -5m,
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.SpreadCollapsed) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));
        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                pos.Status = PositionStatus.Closed;
                pos.RealizedPnl = -5m;
            })
            .Returns(Task.CompletedTask);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — PushPositionRemovalsAsync called with the closed position
        _mockNotifier.Verify(
            n => n.PushPositionRemovalsAsync(
                It.IsAny<IReadOnlyList<(int, string, int, int, PositionStatus)>>(),
                It.Is<List<(int, string)>>(l => l.Any(x => x.Item1 == 55))),
            Times.Once);
    }

    [Fact]
    public async Task OpenPositions_NotRemoved()
    {
        // Arrange — health monitor returns no reaped/closed positions
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthCheckResult.Empty);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        var openPos = new ArbitragePosition
        {
            Id = 77,
            UserId = TestUserId,
            Status = PositionStatus.Open,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
        };
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition> { openPos });
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — PushPositionRemovalsAsync still called but with empty lists
        _mockNotifier.Verify(
            n => n.PushPositionRemovalsAsync(
                It.Is<IReadOnlyList<(int, string, int, int, PositionStatus)>>(l => l.Count == 0),
                It.Is<List<(int, string)>>(l => l.Count == 0)),
            Times.Once);
    }

    [Fact]
    public async Task RebalanceClose_OnlyPushesRemoval_WhenPositionActuallyTransitioned()
    {
        // Arrange — position stays Open after ClosePositionAsync (close failed silently)
        var pos = new ArbitragePosition
        {
            Id = 101,
            UserId = TestUserId,
            Status = PositionStatus.Open,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Lighter" },
        };

        var rebalanceConfig = new BotConfiguration
        {
            IsEnabled = false, // Don't enter user cycle
            OperatingState = BotOperatingState.Stopped,
            RebalanceEnabled = true,
            MaxRebalancesPerCycle = 2,
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(rebalanceConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition> { pos });
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        var mockRebalancer = new Mock<IPortfolioRebalancer>();
        mockRebalancer.Setup(r => r.EvaluateAsync(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<BotConfiguration>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RebalanceRecommendationDto>
            {
                new(101, "ETH", 0.001m, 0.5m, "BTC", "Hyperliquid", "Aster", 0.002m, 0.001m)
            });
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPortfolioRebalancer))).Returns(mockRebalancer.Object);

        // ClosePositionAsync does NOT change the status — simulates a failed close
        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.Rebalanced, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // pos.Status remains Open

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — PushRebalanceRemovalsAsync should NOT be called because position didn't transition
        _mockNotifier.Verify(n => n.PushRebalanceRemovalsAsync(It.IsAny<List<(int, string)>>()), Times.Never);
    }

    // ── Circuit Breaker Tests ────────────────────────────────────────────────

    private static readonly BotConfiguration CircuitBreakerConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        MaxConcurrentPositions = 5,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        UpdatedByUserId = "admin-user-id",
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        TotalCapitalUsdc = 1000m,
        MaxCapitalPerPosition = 0.5m,
        VolumeFraction = 0.001m,
        ExchangeCircuitBreakerThreshold = 3,
        ExchangeCircuitBreakerMinutes = 15,
    };

    private void SetupCircuitBreakerScenario(ArbitrageOpportunityDto opp, bool openSuccess)
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(openSuccess ? (true, (string?)null) : (false, "Connection error"));
    }

    [Fact]
    public async Task CircuitBreaker_TriggersAfterThresholdFailures()
    {
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        SetupCircuitBreakerScenario(opp, openSuccess: false);

        // Fail 3 times (threshold = 3)
        for (int i = 0; i < 3; i++)
        {
            _circuitBreaker.FailedOpCooldowns.Clear(); // Clear per-user cooldowns so the opp isn't skipped
            await _sut.RunCycleAsync(CancellationToken.None);
        }

        // Both exchanges should now be circuit-broken
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(1);
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(2);
        _circuitBreaker.ExchangeCircuitBreaker[1].Failures.Should().BeGreaterThanOrEqualTo(3);
        _circuitBreaker.ExchangeCircuitBreaker[2].Failures.Should().BeGreaterThanOrEqualTo(3);
        _circuitBreaker.ExchangeCircuitBreaker[1].BrokenUntil.Should().BeAfter(DateTime.UtcNow);
        _circuitBreaker.ExchangeCircuitBreaker[2].BrokenUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task CircuitBreaker_SuccessResetsCounter()
    {
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        // Pre-seed 2 failures (below threshold of 3)
        _circuitBreaker.ExchangeCircuitBreaker[2] = (2, DateTime.MinValue);

        SetupCircuitBreakerScenario(opp, openSuccess: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both exchange counters should be reset
        _circuitBreaker.ExchangeCircuitBreaker.Should().NotContainKey(1);
        _circuitBreaker.ExchangeCircuitBreaker.Should().NotContainKey(2);
    }

    [Fact]
    public async Task CircuitBreaker_ExcludesExchangeFromOpportunityFiltering()
    {
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        // Pre-seed exchange 2 as circuit-broken
        _circuitBreaker.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(15));

        SetupCircuitBreakerScenario(opp, openSuccess: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Execution engine should NOT be called — the exchange is circuit-broken
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CircuitBreaker_ResetsAfterTimeout()
    {
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        // Pre-seed exchange 2 as circuit-broken but expired
        _circuitBreaker.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(-1));

        SetupCircuitBreakerScenario(opp, openSuccess: true);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Execution engine SHOULD be called — the circuit breaker has expired
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AdaptiveFallback_ExcludesCircuitBrokenExchanges()
    {
        SetupEnabledUser();

        // Only net-positive opportunities available (below threshold) — triggers adaptive fallback
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.00005m,
            SpreadPerHour = 0.0001m, // Below threshold (0.0001m)
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        // Pre-seed exchange 2 as circuit-broken
        _circuitBreaker.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(15));

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>(), // No above-threshold opportunities
                AllNetPositive = new List<ArbitrageOpportunityDto> { opp }, // Only adaptive fallback candidates
            });

        await _sut.RunCycleAsync(CancellationToken.None);

        // Execution engine should NOT be called — circuit-broken exchange must be excluded from adaptive path
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CircuitBreaker_ExpiredEntries_CleanedUpOnCycleStart()
    {
        // Pre-seed an expired circuit breaker entry
        _circuitBreaker.ExchangeCircuitBreaker[99] = (3, DateTime.UtcNow.AddMinutes(-5));

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Expired entry should have been cleaned up
        _circuitBreaker.ExchangeCircuitBreaker.Should().NotContainKey(99);
    }

    [Fact]
    public async Task AdaptiveFallback_OpenedPosition_SnapshotRecordsWasOpened()
    {
        SetupEnabledUser();

        // The adaptive candidate is below threshold and only in AllNetPositive, NOT in Opportunities
        var adaptiveOpp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.00005m,
            SpreadPerHour = 0.0001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>(), // No above-threshold opportunities
                AllNetPositive = new List<ArbitrageOpportunityDto> { adaptiveOpp },
            });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        // Capture persisted snapshots
        var mockSnapshotRepo = new Mock<IOpportunitySnapshotRepository>();
        List<OpportunitySnapshot>? capturedSnapshots = null;
        mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((snaps, _) => capturedSnapshots = snaps.ToList())
            .Returns(Task.CompletedTask);
        mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(mockSnapshotRepo.Object);

        await _sut.RunCycleAsync(CancellationToken.None);

        // The adaptive candidate should have been opened successfully
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(TestUserId, adaptiveOpp, 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Snapshot for the adaptive candidate should record WasOpened = true, not "below_threshold"
        capturedSnapshots.Should().NotBeNull();
        var adaptiveSnapshot = capturedSnapshots!.FirstOrDefault(s =>
            s.AssetId == 1 && s.LongExchangeId == 1 && s.ShortExchangeId == 2);
        adaptiveSnapshot.Should().NotBeNull("the adaptive candidate should be included in snapshots");
        adaptiveSnapshot!.WasOpened.Should().BeTrue("position was opened via adaptive fallback");
        adaptiveSnapshot.SkipReason.Should().BeNull("opened positions should have null skip reason");
    }

    [Fact]
    public async Task FailedOpCooldowns_ExpiredEntries_CleanedUpOnCycleStart()
    {
        // Pre-seed an entry expired longer than MaxCooldown (60 min) — should be swept
        _circuitBreaker.FailedOpCooldowns["test-user:1_1_2"] = (DateTime.UtcNow.AddMinutes(-65), 2);
        // Seed a recently-expired entry (< MaxCooldown ago) — should survive to preserve failure count
        _circuitBreaker.FailedOpCooldowns["test-user:3_1_2"] = (DateTime.UtcNow.AddMinutes(-5), 1);
        // Seed a non-expired entry — should survive
        _circuitBreaker.FailedOpCooldowns["test-user:2_1_3"] = (DateTime.UtcNow.AddMinutes(5), 1);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Stale entry (expired > MaxCooldown ago) should have been cleaned up
        _circuitBreaker.FailedOpCooldowns.Should().NotContainKey("test-user:1_1_2");
        // Recently-expired entry should survive (failure count preserved for exponential backoff)
        _circuitBreaker.FailedOpCooldowns.Should().ContainKey("test-user:3_1_2");
        // Non-expired entry should survive
        _circuitBreaker.FailedOpCooldowns.Should().ContainKey("test-user:2_1_3");
    }

    // ── Skip Reason Accuracy Tests ───────────────────────────────────────────

    [Fact]
    public async Task SkipReason_ExchangeDisabled()
    {
        SetupEnabledUser();
        // User has exchanges 1,2,3 enabled (from default setup)
        // Opportunity uses exchange 4 which is NOT enabled
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 4,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "UnknownExchange",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Verify the snapshot was persisted with "exchange_disabled" skip reason
        _mockUow.Verify(u => u.OpportunitySnapshots, Times.AtLeastOnce);
    }

    [Fact]
    public async Task SkipReason_CircuitBroken()
    {
        // Pre-seed circuit breaker for exchange 2
        _circuitBreaker.ExchangeCircuitBreaker[2] = (3, DateTime.UtcNow.AddMinutes(15));

        SetupEnabledUser();
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Execution engine should NOT be called — circuit-broken
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SkipReason_NotSelected()
    {
        SetupEnabledUser();
        // Concentrated strategy takes only 1 candidate, second should be "not_selected"
        var opp1 = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.002m,
            SpreadPerHour = 0.002m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        var opp2 = new ArbitrageOpportunityDto
        {
            AssetId = 2,
            AssetSymbol = "BTC",
            LongExchangeId = 1,
            ShortExchangeId = 3,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Aster",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        await _sut.RunCycleAsync(CancellationToken.None);

        // First opp opened, second was "not_selected" by Concentrated strategy
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Reap triggers circuit breaker ────────────────────────────────────────

    [Fact]
    public async Task ReapedPositions_TriggerCircuitBreakerForBothExchanges()
    {
        // Arrange — health monitor returns reaped positions with exchange IDs
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>
        {
            (42, TestUserId, 1, 2, PositionStatus.Closing),
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                reapedPositions,
                new Dictionary<int, ComputedPositionPnl>()));
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — circuit breaker incremented for both exchange IDs
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(1);
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(2);
        _circuitBreaker.ExchangeCircuitBreaker[1].Failures.Should().Be(1);
        _circuitBreaker.ExchangeCircuitBreaker[2].Failures.Should().Be(1);
    }

    // ── Opening positions counted in MaxConcurrentPositions gate ──────────

    [Fact]
    public async Task OpeningPositions_CountedInMaxConcurrentPositionsGate()
    {
        // Arrange — user has 0 Open positions but 1 Opening position, max = 1
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());

        // Return 1 Opening position for the user
        var openingPos = new ArbitragePosition
        {
            Id = 99,
            UserId = TestUserId,
            Status = PositionStatus.Opening,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
        };
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition> { openingPos });

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 2,
            AssetSymbol = "BTC",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — OpenPositionAsync should NOT be called because 0+1 >= MaxConcurrent(1)
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── OpenPositionAsync exception is caught and circuit breaker fires ────

    [Fact]
    public async Task OpenPositionAsync_Exception_CaughtAndCircuitBreakerFires()
    {
        // Arrange
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        // OpenPositionAsync throws an exception instead of returning (false, error)
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Signer not initialized"));

        // Act — should NOT throw (exception is caught internally)
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — circuit breaker incremented for both exchanges
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(1);
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(2);
        _circuitBreaker.ExchangeCircuitBreaker[1].Failures.Should().Be(1);
        _circuitBreaker.ExchangeCircuitBreaker[2].Failures.Should().Be(1);
    }

    // ── NB1: Exception with "balance" in message takes generic failure path ──

    [Fact]
    public async Task OpenPositionAsync_ThrowsWithBalanceMessage_TakesGenericFailurePath()
    {
        // Arrange — exception message incidentally contains "balance"
        SetupEnabledUser();
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 3,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
            });

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());

        var opp1 = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.002m,
            SpreadPerHour = 0.002m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        var opp2 = new ArbitrageOpportunityDto
        {
            AssetId = 2,
            AssetSymbol = "BTC",
            LongExchangeId = 1,
            ShortExchangeId = 3,
            LongExchangeName = "Hyperliquid",
            ShortExchangeName = "Aster",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m, 100m]);
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(new Mock<IOpportunitySnapshotRepository>().Object);

        // First call throws with "balance" in message — should NOT route to capital-exhaustion
        _mockExecEngine.SetupSequence(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Failed to fetch balance from Lighter API"))
            .ReturnsAsync((true, (string?)null));

        // Act
        await _sut.RunCycleAsync(CancellationToken.None);

        // Assert — second opportunity should still be attempted (exception didn't misroute to capital-exhaustion break)
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Exception with 'balance' in message must take generic failure path, not capital-exhaustion break");
    }

    // ── Task 3.1: Opening positions refresh after state change ─────────────────

    [Fact]
    public async Task RunCycle_RefreshesOpeningPositionsAfterStateChange()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyp",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(),
                It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // Simulate: open succeeds, triggering positionsChanged=true
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), opp, 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // After positionsChanged=true, GetByStatusAsync(Opening) should be called again (refresh)
        // Once during initial fetch (constructor default), once during refresh
        _mockPositions.Verify(
            p => p.GetByStatusAsync(PositionStatus.Opening),
            Times.AtLeast(2),
            "Opening positions should be refreshed after positions change state");
    }

    // ── Task 3.1: Exchange circuit breaker on margin error ─────────────────────

    [Fact]
    public async Task RunCycle_MarginError_TripsCircuitBreakerImmediately()
    {
        SetupEnabledUser();
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            DefaultLeverage = 5,
            UpdatedByUserId = "admin",
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            ExchangeCircuitBreakerMinutes = 15,
            ExchangeCircuitBreakerThreshold = 3,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyp",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(),
                It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // Simulate margin error
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), opp, 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin for order"));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Run a second cycle with a different opportunity on the same exchanges
        var opp2 = new ArbitrageOpportunityDto
        {
            AssetId = 2,
            AssetSymbol = "BTC",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "Hyp",
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.002m,
            SpreadPerHour = 0.002m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp2] });

        await _sut.RunCycleAsync(CancellationToken.None);

        // The second cycle should NOT attempt to open — exchanges are circuit-broken
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), opp2, It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Circuit-broken exchanges from margin error should block subsequent opens");
    }

    // ── Dashboard needs-attention count uses CountByStatusesAsync ────────────────

    [Fact]
    public async Task RunCycle_UsesCountByStatusesForNeedsAttentionDashboardKpi()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());
        _mockPositions.Setup(p => p.CountByStatusesAsync(PositionStatus.EmergencyClosed, PositionStatus.Failed))
            .ReturnsAsync(3);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Verify CountByStatusesAsync is used with both EmergencyClosed and Failed
        _mockPositions.Verify(p => p.CountByStatusesAsync(PositionStatus.EmergencyClosed, PositionStatus.Failed), Times.Once);
        _mockPositions.Verify(p => p.GetByStatusAsync(PositionStatus.EmergencyClosed), Times.Never);
        // Verify the count is passed to the dashboard update via notifier
        _mockNotifier.Verify(n => n.PushDashboardUpdateAsync(
            It.IsAny<List<ArbitragePosition>>(),
            It.IsAny<List<ArbitrageOpportunityDto>>(),
            It.IsAny<BotOperatingState>(),
            It.IsAny<int>(),
            3,
            It.IsAny<OpportunityResultDto?>()), Times.Once);
    }

    // ── Opening-reaped positions do NOT trigger circuit breaker ────────────────

    [Fact]
    public async Task ReapedOpeningPositions_DoNotTriggerCircuitBreaker()
    {
        // Arrange: health monitor returns an Opening-reaped position
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>
        {
            (42, TestUserId, 1, 2, PositionStatus.Opening),
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                reapedPositions,
                new Dictionary<int, ComputedPositionPnl>()));
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Circuit breaker should NOT be incremented for Opening-reaped positions
        _circuitBreaker.ExchangeCircuitBreaker.Should().BeEmpty("Opening-reaped positions should not increment exchange circuit breaker");
    }

    [Fact]
    public async Task ReapedClosingPositions_DoTriggerCircuitBreaker()
    {
        // Arrange: health monitor returns a Closing-reaped position
        var reapedPositions = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>
        {
            (42, TestUserId, 1, 2, PositionStatus.Closing),
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                reapedPositions,
                new Dictionary<int, ComputedPositionPnl>()));
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(CircuitBreakerConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Circuit breaker SHOULD be incremented for Closing-reaped positions
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(1);
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(2);
    }

    // ── Asset-level cooldown ──────────────────────────────────────────────────

    [Fact]
    public async Task GenericOpenFailure_IdentifiableExchange_OnlyCoolsLongExchange()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Order rejected by Hyperliquid: rate limit"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _circuitBreaker.AssetExchangeCooldowns.Should().ContainKey((1, 1),
            "long exchange (Hyperliquid) identified in error — should be cooled down");
        _circuitBreaker.AssetExchangeCooldowns[(1, 1)].Failures.Should().Be(1);
        _circuitBreaker.AssetExchangeCooldowns.Should().NotContainKey((1, 2),
            "short exchange (Lighter) not in error — should NOT be cooled down");
    }

    [Fact]
    public async Task GenericOpenFailure_IdentifiableExchange_OnlyCoolsShortExchange()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Connection to Lighter timed out"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _circuitBreaker.AssetExchangeCooldowns.Should().ContainKey((1, 2),
            "short exchange (Lighter) identified in error — should be cooled down");
        _circuitBreaker.AssetExchangeCooldowns[(1, 2)].Failures.Should().Be(1);
        _circuitBreaker.AssetExchangeCooldowns.Should().NotContainKey((1, 1),
            "long exchange (Hyperliquid) not in error — should NOT be cooled down");
    }

    [Fact]
    public async Task GenericOpenFailure_UnidentifiableExchange_CoolsBothExchanges()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Network timeout"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _circuitBreaker.AssetExchangeCooldowns.Should().ContainKey((1, 1),
            "unidentifiable error — both exchanges should be cooled down (long)");
        _circuitBreaker.AssetExchangeCooldowns[(1, 1)].Failures.Should().Be(1);
        _circuitBreaker.AssetExchangeCooldowns.Should().ContainKey((1, 2),
            "unidentifiable error — both exchanges should be cooled down (short)");
        _circuitBreaker.AssetExchangeCooldowns[(1, 2)].Failures.Should().Be(1);
    }

    [Fact]
    public async Task GenericOpenFailure_NullError_CoolsBothExchanges()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2,
            ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected failure"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _circuitBreaker.AssetExchangeCooldowns.Should().ContainKey((1, 1),
            "exception path (null error) — both exchanges should be cooled down (long)");
        _circuitBreaker.AssetExchangeCooldowns[(1, 1)].Failures.Should().Be(1);
        _circuitBreaker.AssetExchangeCooldowns.Should().ContainKey((1, 2),
            "exception path (null error) — both exchanges should be cooled down (short)");
        _circuitBreaker.AssetExchangeCooldowns[(1, 2)].Failures.Should().Be(1);
    }

    [Fact]
    public void AssetExchangeCooldown_ActivatesAfterThreeFailures()
    {
        // Simulate 3 failures for asset=1, exchange=1
        for (int i = 0; i < 3; i++)
        {
            // Use reflection-free access via the exposed internal property
            _circuitBreaker.AssetExchangeCooldowns.AddOrUpdate(
                (1, 1),
                _ => (1, 1 >= 3 ? DateTime.UtcNow.AddMinutes(10) : DateTime.MinValue),
                (_, current) =>
                {
                    var f = current.Failures + 1;
                    return (f, f >= 3 ? DateTime.UtcNow.AddMinutes(10) : current.CooldownUntil);
                });
        }

        _circuitBreaker.AssetExchangeCooldowns.TryGetValue((1, 1), out var cooldown);
        cooldown.Failures.Should().Be(3);
        cooldown.CooldownUntil.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void AssetExchangeCooldown_DoesNotAffectOtherAssets()
    {
        // Cool down asset 1 on exchange 1
        _circuitBreaker.AssetExchangeCooldowns[(1, 1)] = (3, DateTime.UtcNow.AddMinutes(10));

        // Asset 2 on exchange 1 should NOT be cooled down
        _circuitBreaker.AssetExchangeCooldowns.Should().NotContainKey((2, 1),
            "asset-level cooldown should not affect other assets on the same exchange");
    }

    [Fact]
    public void AssetExchangeCooldown_ResetsOnSuccess()
    {
        // Set up cooldown
        _circuitBreaker.AssetExchangeCooldowns[(1, 1)] = (3, DateTime.UtcNow.AddMinutes(10));
        _circuitBreaker.AssetExchangeCooldowns[(1, 2)] = (3, DateTime.UtcNow.AddMinutes(10));

        // Simulate success — remove cooldowns
        _circuitBreaker.AssetExchangeCooldowns.TryRemove((1, 1), out _);
        _circuitBreaker.AssetExchangeCooldowns.TryRemove((1, 2), out _);

        _circuitBreaker.AssetExchangeCooldowns.Should().BeEmpty("asset cooldowns should be cleared on successful open");
    }

    // ── PnL Target Cooldown ──────────────────────────────────────────────────

    [Fact]
    public async Task PnlTargetClose_AppliesCooldown_PreventsImmediateReEntry()
    {
        var pos = new ArbitragePosition
        {
            Id = 1,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            RealizedPnl = null,
        };

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.PnlTargetReached) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.PnlTargetReached, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                pos.Status = PositionStatus.Closed;
                pos.RealizedPnl = 50m; // profitable close
            })
            .Returns(Task.CompletedTask);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Key format matches OpportunityFilter: {userId}:{assetId}_{longExId}_{shortExId}
        var cooldownKey = $"{TestUserId}:1_1_2";
        _circuitBreaker.IsOnCooldown(cooldownKey, out _).Should().BeTrue(
            "a PnlTargetReached close should place the pair on cooldown to prevent immediate re-entry");
    }

    [Fact]
    public async Task LosingClose_AppliesCooldown_AsExistingBehavior()
    {
        var pos = new ArbitragePosition
        {
            Id = 2,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            RealizedPnl = null,
        };

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.SpreadCollapsed) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                pos.Status = PositionStatus.Closed;
                pos.RealizedPnl = -20m; // losing close
            })
            .Returns(Task.CompletedTask);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Existing losing-trade path uses colon-separated key: {userId}:{assetId}:{longExId}:{shortExId}
        var losingKey = $"{TestUserId}:1:1:2";
        _circuitBreaker.FailedOpCooldowns.Should().ContainKey(losingKey,
            "losing-trade close must still register a cooldown under the existing key format");
    }

    [Fact]
    public async Task PnlTargetCooldown_Expires_AllowsReEntry()
    {
        var pos = new ArbitragePosition
        {
            Id = 3,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            RealizedPnl = null,
        };

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.PnlTargetReached) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.PnlTargetReached, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                pos.Status = PositionStatus.Closed;
                pos.RealizedPnl = 50m;
            })
            .Returns(Task.CompletedTask);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Manually expire the PnlTarget cooldown
        var cooldownKey = $"{TestUserId}:1_1_2";
        _circuitBreaker.SetCooldown(cooldownKey, DateTime.UtcNow.AddMinutes(-1), 0);

        _circuitBreaker.IsOnCooldown(cooldownKey, out _).Should().BeFalse(
            "cooldown should not suppress re-entry after the configured duration has elapsed");
    }

    [Fact]
    public async Task PnlTargetClose_DryRun_DoesNotApplyCooldown()
    {
        var pos = new ArbitragePosition
        {
            Id = 4,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            RealizedPnl = null,
            IsDryRun = true,
        };

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.PnlTargetReached) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.PnlTargetReached, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                pos.Status = PositionStatus.Closed;
                pos.RealizedPnl = 50m; // profitable close
            })
            .Returns(Task.CompletedTask);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Key format matches OpportunityFilter: {userId}:{assetId}_{longExId}_{shortExId}
        var cooldownKey = $"{TestUserId}:1_1_2";
        _circuitBreaker.IsOnCooldown(cooldownKey, out _).Should().BeFalse(
            "dry-run positions must not place the pair on cooldown — dry-run trades are simulated and should not affect live re-entry logic");
    }

    [Fact]
    public async Task PnlTargetClose_ExecutionFails_NoCooldownApplied()
    {
        var pos = new ArbitragePosition
        {
            Id = 5,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.Open,
            RealizedPnl = null,
        };

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                new[] { (pos, CloseReason.PnlTargetReached) },
                Array.Empty<(int, string, int, int, PositionStatus)>(),
                new Dictionary<int, ComputedPositionPnl>()));

        // ClosePositionAsync does NOT update pos.Status or pos.RealizedPnl — simulates a partial / failed close
        _mockExecEngine.Setup(e => e.ClosePositionAsync(TestUserId, pos, CloseReason.PnlTargetReached, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Key format matches OpportunityFilter: {userId}:{assetId}_{longExId}_{shortExId}
        var cooldownKey = $"{TestUserId}:1_1_2";
        _circuitBreaker.IsOnCooldown(cooldownKey, out _).Should().BeFalse(
            "if ClosePositionAsync fails to mark the position as Closed with a realized PnL, no cooldown should be applied");
    }
}
