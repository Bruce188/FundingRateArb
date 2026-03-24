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

public class BotOrchestratorMultiUserTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

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
    private readonly Mock<IDashboardClient> _mockGroupClient = new();
    private readonly BotOrchestrator _sut;

    private static readonly BotConfiguration EnabledConfig = new()
    {
        IsEnabled = true,
        MaxConcurrentPositions = 5,
        TotalCapitalUsdc = 1000m,
        UpdatedByUserId = "admin",
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        MaxCapitalPerPosition = 0.5m,
        VolumeFraction = 0.001m,
    };

    public BotOrchestratorMultiUserTests()
    {
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

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(ArbitragePosition, CloseReason)>());
        _mockPositions.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Alert>());
        _mockPositions.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(EnabledConfig);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync([]);

        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
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

    private void SetupUser(string userId, UserConfiguration config)
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(userId)).ReturnsAsync(config);
        _mockUserSettings.Setup(s => s.HasValidCredentialsAsync(userId)).ReturnsAsync(true);
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(userId))
            .ReturnsAsync(new List<int> { 1, 2, 3 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(userId))
            .ReturnsAsync(new List<int> { 1, 2, 3, 4, 5 });
    }

    // ── Multi-user cycle iterates all enabled users ────────────────────────────

    [Fact]
    public async Task RunCycle_IteratesAllEnabledUsers()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA, UserB });

        SetupUser(UserA, new UserConfiguration
        {
            UserId = UserA,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });
        SetupUser(UserB, new UserConfiguration
        {
            UserId = UserB,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "ExA",
            ShortExchangeId = 2,
            ShortExchangeName = "ExB",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both users should have had their configs loaded
        _mockUserSettings.Verify(s => s.GetOrCreateConfigAsync(UserA), Times.Once);
        _mockUserSettings.Verify(s => s.GetOrCreateConfigAsync(UserB), Times.Once);

        // Both users should have attempted position opens
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Skips users without valid credentials ──────────────────────────────────

    [Fact]
    public async Task RunCycle_SkipsUsersWithoutCredentials()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA });

        SetupUser(UserA, new UserConfiguration
        {
            UserId = UserA,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });

        // User A has no valid credentials
        _mockUserSettings.Setup(s => s.HasValidCredentialsAsync(UserA)).ReturnsAsync(false);

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        // No positions should be opened
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Status explanation should be pushed to user's group
        _mockHubClients.Verify(c => c.Group($"user-{UserA}"), Times.AtLeastOnce);
    }

    // ── No enabled users means no user cycles ──────────────────────────────────

    [Fact]
    public async Task RunCycle_NoEnabledUsers_SkipsUserCycles()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string>());

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // Health monitor and signal engine still run (global)
        _mockHealthMonitor.Verify(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockSignalEngine.Verify(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()), Times.Once);

        // No user config loads
        _mockUserSettings.Verify(s => s.GetOrCreateConfigAsync(It.IsAny<string>()), Times.Never);
    }

    // ── Kill switch prevents user cycles ─────────────────────────────────────

    [Fact]
    public async Task RunCycle_KillSwitchDisabled_SkipsAllUserCycles()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(new BotConfiguration { IsEnabled = false });

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA });

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        await _sut.RunCycleAsync(CancellationToken.None);

        // User cycle should never execute when global kill switch is off
        _mockUserSettings.Verify(s => s.GetOrCreateConfigAsync(It.IsAny<string>()), Times.Never);
    }

    // ── Per-user cooldowns are isolated ─────────────────────────────────────────

    [Fact]
    public async Task RunCycle_PerUserCooldowns_AreIsolated()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA, UserB });

        SetupUser(UserA, new UserConfiguration
        {
            UserId = UserA,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });
        SetupUser(UserB, new UserConfiguration
        {
            UserId = UserB,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "ExA",
            ShortExchangeId = 2,
            ShortExchangeName = "ExB",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange error"));

        // Cycle 1: both users fail
        await _sut.RunCycleAsync(CancellationToken.None);

        // Cooldowns should be keyed per-user
        var keyA = $"{UserA}:1_1_2";
        var keyB = $"{UserB}:1_1_2";
        _sut.FailedOpCooldowns.ContainsKey(keyA).Should().BeTrue("user A's failure should register a cooldown");
        _sut.FailedOpCooldowns.ContainsKey(keyB).Should().BeTrue("user B's failure should register a cooldown");

        // Expire only user A's cooldown
        var entryA = _sut.FailedOpCooldowns[keyA];
        _sut.FailedOpCooldowns[keyA] = (DateTime.UtcNow.AddMinutes(-1), entryA.Failures);

        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        // Cycle 2: only user A should retry (B still on cooldown)
        await _sut.RunCycleAsync(CancellationToken.None);

        // User A: 1 fail (cycle 1) + 1 success (cycle 2) = 2 calls
        // User B: 1 fail (cycle 1) + 0 (still on cooldown) = 1 call
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3), "User A retries (cooldown expired) but User B stays on cooldown");
    }

    // ── Per-user consecutive loss tracking ──────────────────────────────────────

    [Fact]
    public void RecordCloseResult_TracksPerUser()
    {
        _sut.RecordCloseResult(-5m, UserA);
        _sut.RecordCloseResult(-5m, UserA);
        _sut.RecordCloseResult(-5m, UserB);

        _sut.UserConsecutiveLosses[UserA].Should().Be(2);
        _sut.UserConsecutiveLosses[UserB].Should().Be(1);

        // Positive PnL resets only that user's counter
        _sut.RecordCloseResult(10m, UserA);
        _sut.UserConsecutiveLosses[UserA].Should().Be(0);
        _sut.UserConsecutiveLosses[UserB].Should().Be(1, "user B's counter should not be affected by user A's reset");
    }

    // ── User failure does not block other users ─────────────────────────────────

    [Fact]
    public async Task RunCycle_UserCycleFailure_DoesNotBlockOtherUsers()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA, UserB });

        // User A throws an exception during config load
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(UserA))
            .ThrowsAsync(new InvalidOperationException("User A config error"));

        SetupUser(UserB, new UserConfiguration
        {
            UserId = UserB,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "ExA",
            ShortExchangeId = 2,
            ShortExchangeName = "ExB",
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);
        _mockExecEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        // Should not throw — user A's error is caught and logged
        await _sut.RunCycleAsync(CancellationToken.None);

        // User B's cycle should still execute
        _mockUserSettings.Verify(s => s.GetOrCreateConfigAsync(UserB), Times.Once);
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Once, "User B's position open should proceed despite User A's failure");
    }

    // ── Opportunities filtered by user preferences ──────────────────────────────

    [Fact]
    public async Task RunCycle_FiltersOpportunitiesByUserPreferences()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA });

        SetupUser(UserA, new UserConfiguration
        {
            UserId = UserA,
            IsEnabled = true,
            MaxConcurrentPositions = 1,
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });

        // User A only has exchanges 1 and 2 enabled (not 3)
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(UserA))
            .ReturnsAsync(new List<int> { 1, 2 });

        // Opportunity uses exchanges 1 and 3 — user doesn't have exchange 3 enabled
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            AssetSymbol = "ETH",
            LongExchangeId = 1,
            LongExchangeName = "ExA",
            ShortExchangeId = 3,
            ShortExchangeName = "ExC", // not in user's enabled list
            NetYieldPerHour = 0.001m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        await _sut.RunCycleAsync(CancellationToken.None);

        // No position should be opened — opportunity filtered out
        _mockExecEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never, "Opportunity should be filtered out when user doesn't have all required exchanges enabled");
    }

    // ── Status explanations go to per-user SignalR groups ────────────────────────

    [Fact]
    public async Task RunCycle_StatusExplanation_GoesToUserGroup()
    {
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { UserA });

        SetupUser(UserA, new UserConfiguration
        {
            UserId = UserA,
            IsEnabled = true,
            MaxConcurrentPositions = 0, // max reached immediately
            OpenThreshold = 0.0001m,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            AllocationStrategy = AllocationStrategy.Concentrated,
        });

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new OpportunityResultDto());

        var statusSentToUserGroup = false;
        _mockGroupClient
            .Setup(d => d.ReceiveStatusExplanation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => statusSentToUserGroup = true)
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        statusSentToUserGroup.Should().BeTrue(
            "status explanations for per-user gates must be sent to the user's SignalR group");
        _mockHubClients.Verify(c => c.Group($"user-{UserA}"), Times.AtLeastOnce);
    }

    // ── RecordCloseResult with null userId is ignored ────────────────────────────

    [Fact]
    public void RecordCloseResult_NullUserId_DoesNothing()
    {
        _sut.RecordCloseResult(-10m, null);

        _sut.UserConsecutiveLosses.Should().BeEmpty(
            "RecordCloseResult with null userId should not create any entries");
    }
}
