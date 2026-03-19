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
        // H3: Disabled path still fetches positions once for dashboard push
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockSignalEngine.Verify(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockHealthMonitor.Verify(h => h.CheckAndActAsync(), Times.Never);
    }

    // ── Health monitor called first ────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_CallsHealthMonitorFirst()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var callOrder = new List<string>();
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync())
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

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockSignalEngine.Verify(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()), Times.Never);
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

    // ── H-BO1: Dashboard aggregates go to Admins group ────────────────────────

    [Fact]
    public async Task RunCycle_PushesDashboardUpdate_ToAdminsGroup()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var dashboardSentToAdmins = false;
        _mockGroupClient
            .Setup(d => d.ReceiveDashboardUpdate(It.IsAny<DashboardDto>()))
            .Callback(() => dashboardSentToAdmins = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        dashboardSentToAdmins.Should().BeTrue(
            "dashboard KPI updates must be sent to the Admins group");

        _mockHubClients.Verify(
            c => c.Group(HubGroups.Admins),
            Times.AtLeastOnce);
    }

    // ── M-BO2: SemaphoreSlim is disposed when orchestrator is disposed ─────────

    [Fact]
    public void BotOrchestrator_Dispose_DoesNotThrow()
    {
        // Verifies that Dispose() works correctly (SemaphoreSlim is disposed)
        var act = () => _sut.Dispose();
        act.Should().NotThrow();
    }
}
