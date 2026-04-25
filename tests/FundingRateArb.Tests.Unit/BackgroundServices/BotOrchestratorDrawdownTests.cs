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

public class BotOrchestratorDrawdownTests
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
    private readonly BotOrchestrator _sut;

    private static readonly BotConfiguration EnabledConfig = new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        MaxConcurrentPositions = 5,
        MaxLeverageCap = 50,
        TotalCapitalUsdc = 10_000m,
        UpdatedByUserId = "admin",
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        MaxCapitalPerPosition = 0.5m,
        VolumeFraction = 0.001m,
    };

    private static readonly UserConfiguration UserConfig = new()
    {
        UserId = TestUserId,
        IsEnabled = true,
        MaxConcurrentPositions = 5,
        DefaultLeverage = 5,
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        TotalCapitalUsdc = 10_000m,
        MaxCapitalPerPosition = 0.5m,
        OpenThreshold = 0.0001m,
        DailyDrawdownPausePct = 0.05m,
        ConsecutiveLossPause = 3,
    };

    public BotOrchestratorDrawdownTests()
    {
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);

        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(ISignalEngine))).Returns(_mockSignalEngine.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPositionSizer))).Returns(_mockPositionSizer.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IBalanceAggregator))).Returns(_mockBalanceAggregator.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IExecutionEngine))).Returns(_mockExecEngine.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPositionHealthMonitor))).Returns(_mockHealthMonitor.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IUserSettingsService))).Returns(_mockUserSettings.Object);

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);

        _mockExchanges.Setup(e => e.GetAllAsync()).ReturnsAsync(new List<Exchange>());
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>())).ReturnsAsync(HealthCheckResult.Empty);
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening)).ReturnsAsync(new List<ArbitragePosition>());
        _mockPositions.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>())).ReturnsAsync(0);
        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>())).ReturnsAsync(new List<Alert>());
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<ArbitragePosition>());
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync()).ReturnsAsync(new List<string> { TestUserId });
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId)).ReturnsAsync(UserConfig);
        _mockUserSettings.Setup(s => s.HasValidCredentialsAsync(TestUserId)).ReturnsAsync(true);
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId)).ReturnsAsync(new List<int> { 1, 2 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId)).ReturnsAsync(new List<int> { 1, 2, 3 });
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync()).ReturnsAsync(new List<int>());

        _mockNotifier.Setup(n => n.PushDashboardUpdateAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<List<ArbitrageOpportunityDto>>(), It.IsAny<BotOperatingState>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<OpportunityResultDto?>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushPositionUpdatesAsync(It.IsAny<List<ArbitragePosition>>(), It.IsAny<BotConfiguration>(), It.IsAny<IReadOnlyDictionary<int, ComputedPositionPnl>?>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushPositionRemovalsAsync(It.IsAny<IReadOnlyList<(int, string, int, int, PositionStatus)>>(), It.IsAny<List<(int, string)>>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushRebalanceRemovalsAsync(It.IsAny<List<(int, string)>>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushNewAlertsAsync(It.IsAny<IUnitOfWork>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushStatusExplanationAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushOpportunityUpdateAsync(It.IsAny<OpportunityResultDto>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushBalanceUpdateAsync(It.IsAny<string>(), It.IsAny<BalanceSnapshotDto>())).Returns(Task.CompletedTask);
        _mockNotifier.Setup(n => n.PushNotificationAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _mockReadinessSignal.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var circuitBreaker = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var opportunityFilter = new OpportunityFilter(circuitBreaker, NullLogger<OpportunityFilter>.Instance);

        _sut = new BotOrchestrator(
            _mockScopeFactory.Object,
            _mockReadinessSignal.Object,
            _mockNotifier.Object,
            circuitBreaker,
            opportunityFilter,
            _mockRotationEvaluator.Object,
            NullLogger<BotOrchestrator>.Instance);
    }

    [Fact]
    public async Task DrawdownLimit_EqualsStartOfDayLiveEquityTimesPct()
    {
        // Start-of-day equity = 8 000 USDC; drawdown pct = 5% → limit = 400 USDC
        // Loss = -500 USDC → exceeds limit → gate should trigger
        _mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 8_000m, FetchedAt = DateTime.UtcNow });

        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { UserId = TestUserId, RealizedPnl = -500m, Status = PositionStatus.Closed },
            });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Drawdown gate should block position opens when loss exceeds start-of-day equity × pct");

        _sut._startOfDayLiveEquity.Should().Be(8_000m);
    }

    [Fact]
    public async Task DayRollover_ReCapturesStartOfDayLiveEquity()
    {
        // First cycle: equity = 8 000
        _mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 8_000m, FetchedAt = DateTime.UtcNow });

        await _sut.RunCycleAsync(CancellationToken.None);
        _sut._startOfDayLiveEquity.Should().Be(8_000m);

        // Simulate day rollover by backdating the captured date
        _sut._startOfDayCapturedFor = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        // Second cycle: equity = 12 000 (new day's balance)
        _mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 12_000m, FetchedAt = DateTime.UtcNow });

        await _sut.RunCycleAsync(CancellationToken.None);

        _sut._startOfDayLiveEquity.Should().Be(12_000m, "day rollover should re-capture live equity");
        _sut._startOfDayCapturedFor.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public async Task StartOfDayFetchFailsAndNoPriorValue_DrawdownGateNotTriggered()
    {
        // Balance fetch throws — no prior SOD value exists → gate must be skipped (fail-open)
        _mockBalanceAggregator
            .Setup(b => b.GetBalanceSnapshotAsync(TestUserId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("exchange unreachable"));

        // Massive loss that would normally exceed any reasonable drawdown limit
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { UserId = TestUserId, RealizedPnl = -99_999m, Status = PositionStatus.Closed },
            });

        // Provide an opportunity so the cycle has something to evaluate after gate passes
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
        _mockSignalEngine
            .Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer
            .Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine
            .Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        _sut._startOfDayLiveEquity.Should().BeNull("fetch failed with no prior value — field stays null");

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "drawdown gate should be skipped (fail-open) when SOD equity is unavailable");
    }
}
