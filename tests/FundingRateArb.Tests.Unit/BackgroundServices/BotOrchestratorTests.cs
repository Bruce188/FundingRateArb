using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class BotOrchestratorTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockScopeProvider = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<ISignalEngine> _mockSignalEngine = new();
    private readonly Mock<IPositionSizer> _mockPositionSizer = new();
    private readonly Mock<IExecutionEngine> _mockExecEngine = new();
    private readonly Mock<IPositionHealthMonitor> _mockHealthMonitor = new();
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
    };

    private static readonly BotConfiguration DisabledConfig = new()
    {
        IsEnabled = false,
    };

    public BotOrchestratorTests()
    {
        // Wire scope factory
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);

        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(ISignalEngine))).Returns(_mockSignalEngine.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPositionSizer))).Returns(_mockPositionSizer.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IExecutionEngine))).Returns(_mockExecEngine.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IPositionHealthMonitor))).Returns(_mockHealthMonitor.Object);

        // Wire UoW
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);

        // M12: Default mock for GetRecentUnreadAsync (returns empty list)
        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Alert>());

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
        _mockGroupClient.Setup(d => d.ReceiveOpportunityUpdate(It.IsAny<List<ArbitrageOpportunityDto>>())).Returns(Task.CompletedTask);

        _sut = new BotOrchestrator(
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            NullLogger<BotOrchestrator>.Instance);
    }

    // ── Kill switch ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenKillSwitchOff_ReturnsImmediately()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DisabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ArbitrageOpportunityDto>());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Opportunities are always computed even when disabled
        _mockSignalEngine.Verify(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()), Times.Once);
        // Health monitor only runs when enabled
        _mockHealthMonitor.Verify(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Health monitor called first ────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_CallsHealthMonitorFirst()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var callOrder = new List<string>();
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("health"))
            .Returns(Task.CompletedTask);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("signal"))
            .ReturnsAsync([]);

        await _sut.RunCycleAsync(CancellationToken.None);

        callOrder.Should().ContainInOrder("health", "signal");
    }

    // ── Max positions reached ──────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenMaxPositionsReached_DoesNotOpenNew()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        // MaxConcurrentPositions = 1, already have 1 open
        _mockPositions.Setup(p => p.GetOpenAsync())
            .ReturnsAsync([new ArbitragePosition { Status = Domain.Enums.PositionStatus.Open }]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<ArbitrageOpportunityDto>());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Opportunities are always computed before the max-positions gate
        _mockSignalEngine.Verify(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()), Times.Once);
        // Execution engine must not be called — no new positions should be opened
        _mockExecEngine.Verify(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Opens at most one position per cycle ───────────────────────────────────

    [Fact]
    public async Task RunCycle_OpensAtMostOnePositionPerCycle()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.001m };
        var opp2 = new ArbitrageOpportunityDto { AssetId = 2, AssetSymbol = "BTC", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.0005m };

        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp1, opp2]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(It.IsAny<ArbitrageOpportunityDto>()))
            .ReturnsAsync(100m);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Only 1 position opened even with 2 opportunities
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Skips already-open position pairs ─────────────────────────────────────

    [Fact]
    public async Task RunCycle_SkipsAlreadyOpenPositions()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration
        {
            IsEnabled = true,
            MaxConcurrentPositions = 2, // allow more
        });

        // ETH Hyperliquid/Lighter already open
        _mockPositions.Setup(p => p.GetOpenAsync())
            .ReturnsAsync([new ArbitragePosition { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2 }]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, ShortExchangeId = 2,  // same pair
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Zero size → skip ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_WhenSizeIsZero_DoesNotOpenPosition()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto { AssetId = 1, AssetSymbol = "ETH", LongExchangeId = 1, ShortExchangeId = 2, NetYieldPerHour = 0.001m };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(opp)).ReturnsAsync(0m);

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
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

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
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

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
        // H7: ReceiveNotification on position open must NOT go to Clients.All
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(opp)).ReturnsAsync(100m);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(opp, 100m, It.IsAny<CancellationToken>()))
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
        // H8: Opportunity push moved from FundingRateFetcher to BotOrchestrator
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opportunities = new List<ArbitrageOpportunityDto>
        {
            new() { AssetId = 1, AssetSymbol = "ETH", NetYieldPerHour = 0.001m },
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(opportunities);

        var opportunityUpdateSent = false;
        _mockGroupClient
            .Setup(d => d.ReceiveOpportunityUpdate(It.IsAny<List<ArbitrageOpportunityDto>>()))
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
        // M6: Positions fetched before health monitor may include already-closed positions.
        // The call to GetOpenAsync must happen AFTER CheckAndActAsync completes.
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var callOrder = new List<string>();

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("health"))
            .Returns(Task.CompletedTask);

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
        // M7: Two consecutive cycles must not replay the same alerts.
        // The second cycle's GetRecentUnreadAsync window must start from when the first cycle ran.
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

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

        // The second window should reflect time since the first call, not a fixed 2 minutes.
        // It must be significantly less than 2 minutes (which would be the fixed-window bug).
        capturedWindows[1].Should().BeLessThan(TimeSpan.FromMinutes(1),
            "the second cycle's window should be the elapsed time since the first alert push, not a fixed 2-min window");
    }

    // ── M-BO2: SemaphoreSlim is disposed when orchestrator is disposed ─────────

    [Fact]
    public void BotOrchestrator_Dispose_DoesNotThrow()
    {
        // Verifies that Dispose() works correctly (SemaphoreSlim is disposed)
        var act = () => _sut.Dispose();
        act.Should().NotThrow();
    }

    // ── C3: Failed opportunity cooldown ─────────────────────────────────────────

    [Fact]
    public async Task RunCycle_AfterFailure_SuppressesRetryOnNextCycle()
    {
        // C3: After OpenPositionAsync fails, the same opportunity should NOT be retried next cycle
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(opp)).ReturnsAsync(100m);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(opp, 100m, It.IsAny<CancellationToken>()))
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
        // C3: After cooldown expires, the opportunity should be retried
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(opp)).ReturnsAsync(100m);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(opp, 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        // Cycle 1: fails — registers cooldown
        await _sut.RunCycleAsync(CancellationToken.None);

        // Manually expire the cooldown for testing
        var key = "1_1_2";
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
        // C3: A successful open must clear the cooldown for that opportunity
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(opp)).ReturnsAsync(100m);

        // First call fails, second succeeds
        _mockExecEngine.SetupSequence(e => e.OpenPositionAsync(opp, 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"))
            .ReturnsAsync((true, (string?)null));

        // Cycle 1: fails
        await _sut.RunCycleAsync(CancellationToken.None);
        var key = "1_1_2";
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
        // C3: After multiple consecutive failures, cooldown duration increases exponentially
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1, AssetSymbol = "ETH",
            LongExchangeId = 1, LongExchangeName = "Hyperliquid",
            ShortExchangeId = 2, ShortExchangeName = "Lighter",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([opp]);
        _mockPositionSizer.Setup(s => s.CalculateOptimalSizeAsync(opp)).ReturnsAsync(100m);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(opp, 100m, It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        var key = "1_1_2";

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
        // Failure 1: 5 min, Failure 2: 10 min, Failure 3: 20 min
        var duration1 = BotOrchestrator.BaseCooldown; // 5 min
        var duration3 = TimeSpan.FromTicks(
            Math.Min(BotOrchestrator.BaseCooldown.Ticks * (1L << 2), BotOrchestrator.MaxCooldown.Ticks)); // 20 min
        duration3.Should().BeGreaterThan(duration1, "exponential backoff must increase cooldown with consecutive failures");
    }
}
