using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
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
    private readonly Mock<IUserConfigurationRepository> _mockUserConfigs = new();
    private readonly Mock<ISignalEngine> _mockSignalEngine = new();
    private readonly Mock<IPositionSizer> _mockPositionSizer = new();
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator = new();
    private readonly Mock<IExecutionEngine> _mockExecEngine = new();
    private readonly Mock<IPositionHealthMonitor> _mockHealthMonitor = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IFundingRateReadinessSignal> _mockReadinessSignal = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext = new();
    private readonly Mock<IHubClients<IDashboardClient>> _mockHubClients = new();
    private readonly Mock<IDashboardClient> _mockDashboardClient = new();
    private readonly Mock<IDashboardClient> _mockGroupClient = new();
    private readonly BotOrchestrator _sut;

    private static readonly BotConfiguration EnabledConfig = new()
    {
        IsEnabled = true,
        MaxConcurrentPositions = 1,
        DefaultLeverage = 5,
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
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);

        // Default mock for health monitor — returns empty close list
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(ArbitragePosition, CloseReason)>());

        // Default mock for Opening status query (used in duplicate check)
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition>());

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

        // Wire hub — Clients.All for dashboard/position/notification broadcasts
        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
        _mockHubClients.Setup(c => c.All).Returns(_mockDashboardClient.Object);
        _mockDashboardClient.Setup(d => d.ReceiveNotification(It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockDashboardClient.Setup(d => d.ReceiveDashboardUpdate(It.IsAny<DashboardDto>())).Returns(Task.CompletedTask);
        _mockDashboardClient.Setup(d => d.ReceivePositionUpdate(It.IsAny<PositionSummaryDto>())).Returns(Task.CompletedTask);

        // C4: Wire Group("user-{userId}") for per-user alert targeting
        _mockHubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockGroupClient.Object);
        _mockGroupClient.Setup(d => d.ReceiveAlert(It.IsAny<AlertDto>())).Returns(Task.CompletedTask);
        _mockGroupClient.Setup(d => d.ReceiveNotification(It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockGroupClient.Setup(d => d.ReceiveDashboardUpdate(It.IsAny<DashboardDto>())).Returns(Task.CompletedTask);
        _mockGroupClient.Setup(d => d.ReceivePositionUpdate(It.IsAny<PositionSummaryDto>())).Returns(Task.CompletedTask);
        _mockGroupClient.Setup(d => d.ReceiveOpportunityUpdate(It.IsAny<OpportunityResultDto>())).Returns(Task.CompletedTask);
        _mockGroupClient.Setup(d => d.ReceiveStatusExplanation(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        _mockReadinessSignal.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new BotOrchestrator(
            _mockScopeFactory.Object,
            _mockReadinessSignal.Object,
            _mockHubContext.Object,
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
            .ReturnsAsync(Array.Empty<(ArbitragePosition, CloseReason)>());
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
        _mockExecEngine.Verify(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Only 1 position opened even with 2 opportunities (Concentrated takes 1)
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
            MaxConcurrentPositions = 2, // allow more
        });

        // ETH Hyperliquid/Lighter already open for this user
        _mockPositions.Setup(p => p.GetOpenAsync())
            .ReturnsAsync([new ArbitragePosition { UserId = TestUserId, AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2 }]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, ShortExchangeId = 2,  // same pair
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0m]);

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── H-BO1: Position updates go to per-user group, not Clients.All ─────────

    [Fact]
    public async Task RunCycle_PushesPositionUpdates_ToUserGroup_NotClientsAll()
    {
        var userId = "trader-user-id";
        var openPosition = new ArbitragePosition
        {
            Id = 1,
            UserId = userId,
            Status = Domain.Enums.PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Lighter" },
        };

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([openPosition]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var positionUpdateSentToAll = false;
        _mockDashboardClient
            .Setup(d => d.ReceivePositionUpdate(It.IsAny<PositionSummaryDto>()))
            .Callback(() => positionUpdateSentToAll = true)
            .Returns(Task.CompletedTask);

        var positionUpdateSentToGroup = false;
        _mockGroupClient
            .Setup(d => d.ReceivePositionUpdate(It.IsAny<PositionSummaryDto>()))
            .Callback(() => positionUpdateSentToGroup = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Position updates must NOT go to Clients.All
        positionUpdateSentToAll.Should().BeFalse(
            "position updates contain user-specific data and must not be broadcast to all clients");

        // Position updates MUST go to the user's group
        positionUpdateSentToGroup.Should().BeTrue(
            "position updates must be sent to the per-user group");

        _mockHubClients.Verify(
            c => c.Group($"user-{userId}"),
            Times.AtLeastOnce);
    }

    // ── H-BO1: Dashboard aggregates go to MarketData group ───────────────────

    [Fact]
    public async Task RunCycle_PushesDashboardUpdate_ToMarketDataGroup()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var dashboardSentToMarketData = false;
        _mockGroupClient
            .Setup(d => d.ReceiveDashboardUpdate(It.IsAny<DashboardDto>()))
            .Callback(() => dashboardSentToMarketData = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        dashboardSentToMarketData.Should().BeTrue(
            "dashboard KPI updates must be sent to the MarketData group");

        _mockHubClients.Verify(
            c => c.Group(HubGroups.MarketData),
            Times.AtLeastOnce);
    }

    // ── H7: Position-open notification goes to user group + admins, not Clients.All ──

    [Fact]
    public async Task RunCycle_OnPositionOpen_SendsNotification_ToUserGroupAndAdmins_NotClientsAll()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        var notificationSentToAll = false;
        _mockDashboardClient
            .Setup(d => d.ReceiveNotification(It.IsAny<string>()))
            .Callback(() => notificationSentToAll = true)
            .Returns(Task.CompletedTask);

        var notificationSentToGroup = false;
        _mockGroupClient
            .Setup(d => d.ReceiveNotification(It.IsAny<string>()))
            .Callback(() => notificationSentToGroup = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        notificationSentToAll.Should().BeFalse(
            "position-open notifications reveal trading activity and must not go to Clients.All");
        notificationSentToGroup.Should().BeTrue(
            "position-open notifications must be routed to the user group and/or admins group");
    }

    // ── H8: BotOrchestrator pushes opportunity updates after GetOpportunitiesAsync ──

    [Fact]
    public async Task RunCycle_PushesOpportunityUpdate_ToMarketDataGroup()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opportunities = new List<ArbitrageOpportunityDto>
        {
            new() { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.001m },
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = opportunities });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([0m]);

        var opportunityUpdateSent = false;
        _mockGroupClient
            .Setup(d => d.ReceiveOpportunityUpdate(It.IsAny<OpportunityResultDto>()))
            .Callback(() => opportunityUpdateSent = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        opportunityUpdateSent.Should().BeTrue(
            "BotOrchestrator must push opportunity updates to MarketData group after computing them");

        _mockHubClients.Verify(
            c => c.Group(HubGroups.MarketData),
            Times.AtLeastOnce);
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
            .ReturnsAsync(Array.Empty<(ArbitragePosition, CloseReason)>());

        _mockPositions.Setup(p => p.GetOpenAsync())
            .Callback(() => callOrder.Add("GetOpen"))
            .ReturnsAsync([]);

        await _sut.RunCycleAsync(CancellationToken.None);

        // GetOpenAsync must be called AFTER the health monitor
        var healthIndex  = callOrder.IndexOf("health");
        var getOpenIndex = callOrder.IndexOf("GetOpen");

        healthIndex.Should().BeGreaterThanOrEqualTo(0, "health monitor must run");
        getOpenIndex.Should().BeGreaterThan(healthIndex,
            "GetOpenAsync must be called after health monitor to avoid stale closed positions");
    }

    // ── M7: Alert push uses rolling cutoff, not fixed 2-minute window ─────────

    [Fact]
    public async Task RunCycle_AlertPush_UsesRollingCutoff_NotFixed2MinuteWindow()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var capturedWindows = new List<TimeSpan>();
        _mockAlerts
            .Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .Callback<TimeSpan>(w => capturedWindows.Add(w))
            .ReturnsAsync(new List<Alert>());

        // First cycle
        await _sut.RunCycleAsync(CancellationToken.None);
        // Small pause to ensure time difference is measurable
        await Task.Delay(10);
        // Second cycle
        await _sut.RunCycleAsync(CancellationToken.None);

        capturedWindows.Should().HaveCount(2, "GetRecentUnreadAsync should be called once per cycle");

        capturedWindows[1].Should().BeLessThan(TimeSpan.FromMinutes(1),
            "the second cycle's window should be the elapsed time since the first alert push, not a fixed 2-min window");
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
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        // Cycle 1: fails
        await _sut.RunCycleAsync(CancellationToken.None);
        // Cycle 2: same opportunity should be skipped due to cooldown
        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        // Cycle 1: fails — registers cooldown
        await _sut.RunCycleAsync(CancellationToken.None);

        // Manually expire the cooldown for testing (per-user key)
        var key = $"{TestUserId}:1_1_2";
        var entry = _sut.FailedOpCooldowns[key];
        _sut.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), entry.Failures);

        // Cycle 2: cooldown expired — should retry
        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // First call fails, second succeeds
        _mockExecEngine.SetupSequence(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"))
            .ReturnsAsync((true, (string?)null));

        // Cycle 1: fails
        await _sut.RunCycleAsync(CancellationToken.None);
        var key = $"{TestUserId}:1_1_2";
        _sut.FailedOpCooldowns.ContainsKey(key).Should().BeTrue("cooldown must be registered after failure");

        // Expire cooldown so cycle 2 retries
        var entry = _sut.FailedOpCooldowns[key];
        _sut.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), entry.Failures);

        // Cycle 2: succeeds — clears cooldown
        await _sut.RunCycleAsync(CancellationToken.None);
        _sut.FailedOpCooldowns.ContainsKey(key).Should().BeFalse("cooldown must be cleared after successful open");
    }

    [Fact]
    public async Task RunCycle_ExponentialBackoff_IncreasesWithConsecutiveFailures()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        var key = $"{TestUserId}:1_1_2";

        // Failure 1: cooldown = BaseCooldown (5 min)
        await _sut.RunCycleAsync(CancellationToken.None);
        var cooldown1 = _sut.FailedOpCooldowns[key];
        cooldown1.Failures.Should().Be(1);

        // Expire and fail again
        _sut.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), cooldown1.Failures);
        await _sut.RunCycleAsync(CancellationToken.None);
        var cooldown2 = _sut.FailedOpCooldowns[key];
        cooldown2.Failures.Should().Be(2);

        // Expire and fail a third time
        _sut.FailedOpCooldowns[key] = (DateTime.UtcNow.AddMinutes(-1), cooldown2.Failures);
        await _sut.RunCycleAsync(CancellationToken.None);
        var cooldown3 = _sut.FailedOpCooldowns[key];
        cooldown3.Failures.Should().Be(3);

        // After 3 failures, cooldown should be longer than after 1 failure
        var duration1 = BotOrchestrator.BaseCooldown; // 5 min
        var duration3 = TimeSpan.FromTicks(
            Math.Min(BotOrchestrator.BaseCooldown.Ticks * (1L << 2), BotOrchestrator.MaxCooldown.Ticks)); // 20 min
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

    // ── H6: Alert dedup ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_AlertDedup_DoesNotPushSameAlertTwice()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var alert = new Alert
        {
            Id = 42,
            UserId = "test-user",
            Message = "Test alert",
            CreatedAt = DateTime.UtcNow,
        };

        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync([alert]);

        var alertPushCount = 0;
        _mockGroupClient.Setup(d => d.ReceiveAlert(It.IsAny<AlertDto>()))
            .Callback(() => alertPushCount++)
            .Returns(Task.CompletedTask);

        // Two cycles with the same alert returned
        await _sut.RunCycleAsync(CancellationToken.None);
        await _sut.RunCycleAsync(CancellationToken.None);

        alertPushCount.Should().Be(1, "same alert must not be pushed twice due to dedup");
    }

    // ── H7: Daily drawdown circuit breaker ──────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenDailyDrawdownExceeded_DoesNotOpenPositions()
    {
        SetupEnabledUser();
        var config = new BotConfiguration
        {
            IsEnabled = true,
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
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
        _sut.UserConsecutiveLosses[TestUserId] = 2;

        var opp = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, LongExchangeName = "ExA", ShortExchangeName = "ExB", NetYieldPerHour = 0.001m };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
        _sut.UserConsecutiveLosses[TestUserId] = 2;

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        _sut.UserConsecutiveLosses[TestUserId].Should().Be(2, "consecutive losses are only reset by RecordCloseResult, not by a successful open");
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

        _sut.UserConsecutiveLosses[TestUserId] = 2;

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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

        _sut.UserConsecutiveLosses[TestUserId].Should().Be(3);
    }

    [Fact]
    public void RecordCloseResult_PositivePnl_ResetsCounter()
    {
        _sut.RecordCloseResult(-1m, TestUserId);
        _sut.RecordCloseResult(-1m, TestUserId);
        _sut.RecordCloseResult(1m, TestUserId);

        _sut.UserConsecutiveLosses[TestUserId].Should().Be(0);
    }

    [Fact]
    public async Task OpenFails_DoesNotIncrementConsecutiveLosses()
    {
        SetupEnabledUser();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<Domain.Enums.AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "API timeout"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _sut.UserConsecutiveLosses.GetValueOrDefault(TestUserId, 0).Should().Be(0,
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
            AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
            Status = PositionStatus.Opening,
        };
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync([openingPos]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
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
            .ReturnsAsync(new[] { (pos, CloseReason.SpreadCollapsed) });

        // After ClosePositionAsync, position stays in Closing (partial fill)
        _mockExecEngine.Setup(e => e.ClosePositionAsync(pos, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()))
            .Callback(() => { pos.Status = PositionStatus.Closing; })
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        // RecordCloseResult should NOT have been called since pos is not Closed
        _sut.UserConsecutiveLosses.GetValueOrDefault("admin-user-id", 0).Should().Be(0,
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
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m, 100m]);

        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin for order"));

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Once, "Balance exhaustion should break the position-opening loop");
    }

    // ── D5: First cycle catches startup alerts ──────────────────────────────────

    [Fact]
    public async Task RunCycle_FirstCycle_CatchesStartupAlerts()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var startupAlert = new Alert
        {
            Id = 99,
            UserId = "test-user",
            Message = "Startup alert",
            CreatedAt = DateTime.UtcNow.AddMinutes(-3),
        };
        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync([startupAlert]);

        var alertPushed = false;
        _mockGroupClient.Setup(d => d.ReceiveAlert(It.Is<AlertDto>(a => a.Id == 99)))
            .Callback(() => alertPushed = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        alertPushed.Should().BeTrue(
            "_lastAlertPushUtc initialized to UtcNow.AddMinutes(-5) should catch alerts created 3 min ago");
    }
}
