using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using FundingRateArb.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class SignalRNotifierTests
{
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext;
    private readonly Mock<IHubClients<IDashboardClient>> _mockClients;
    private readonly Mock<IDashboardClient> _mockMarketDataClient;
    private readonly Mock<IDashboardClient> _mockAdminsClient;
    private readonly SignalRNotifier _sut;
    private readonly Dictionary<string, Mock<IDashboardClient>> _groupClients = new();

    public SignalRNotifierTests()
    {
        _mockHubContext = new Mock<IHubContext<DashboardHub, IDashboardClient>>();
        _mockClients = new Mock<IHubClients<IDashboardClient>>();
        _mockMarketDataClient = new Mock<IDashboardClient>();
        _mockAdminsClient = new Mock<IDashboardClient>();

        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockClients.Setup(c => c.Group(HubGroups.MarketData)).Returns(_mockMarketDataClient.Object);
        _mockClients.Setup(c => c.Group(HubGroups.Admins)).Returns(_mockAdminsClient.Object);

        // Dynamic group resolution for user-specific groups
        _mockClients.Setup(c => c.Group(It.Is<string>(g => g.StartsWith("user-"))))
            .Returns((string groupName) =>
            {
                if (!_groupClients.TryGetValue(groupName, out var client))
                {
                    client = new Mock<IDashboardClient>();
                    // Default setups for returned client
                    client.Setup(c => c.ReceiveAlert(It.IsAny<AlertDto>())).Returns(Task.CompletedTask);
                    client.Setup(c => c.ReceiveStatusExplanation(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
                    client.Setup(c => c.ReceiveNotification(It.IsAny<string>())).Returns(Task.CompletedTask);
                    client.Setup(c => c.ReceiveBalanceUpdate(It.IsAny<BalanceSnapshotDto>())).Returns(Task.CompletedTask);
                    client.Setup(c => c.ReceivePositionUpdate(It.IsAny<PositionSummaryDto>())).Returns(Task.CompletedTask);
                    client.Setup(c => c.ReceivePositionRemoval(It.IsAny<int>())).Returns(Task.CompletedTask);
                    _groupClients[groupName] = client;
                }
                return client.Object;
            });

        // Default setups
        _mockMarketDataClient.Setup(c => c.ReceiveOpportunityUpdate(It.IsAny<OpportunityResultDto>())).Returns(Task.CompletedTask);
        _mockMarketDataClient.Setup(c => c.ReceiveDashboardUpdate(It.IsAny<DashboardDto>())).Returns(Task.CompletedTask);
        _mockMarketDataClient.Setup(c => c.ReceiveStatusExplanation(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _mockAdminsClient.Setup(c => c.ReceiveNotification(It.IsAny<string>())).Returns(Task.CompletedTask);

        _sut = new SignalRNotifier(_mockHubContext.Object, NullLogger<SignalRNotifier>.Instance);
    }

    private Mock<IDashboardClient> GetUserClient(string userId) =>
        _groupClients[$"user-{userId}"];

    // ── PushNewAlertsAsync — Deduplication (B2) ─────────────────────────────

    [Fact]
    public async Task PushNewAlertsAsync_DeduplicatesAlerts()
    {
        var alert = new Alert { Id = 1, UserId = "user1", Type = AlertType.SpreadWarning, Severity = AlertSeverity.Warning, Message = "Test", CreatedAt = DateTime.UtcNow };
        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.Alerts.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Alert> { alert });

        await _sut.PushNewAlertsAsync(mockUow.Object);
        await _sut.PushNewAlertsAsync(mockUow.Object);

        // Alert with Id=1 should only be pushed once across both calls
        GetUserClient("user1").Verify(
            c => c.ReceiveAlert(It.Is<AlertDto>(dto => dto.Id == 1)),
            Times.Once);
    }

    [Fact]
    public async Task PushNewAlertsAsync_UsesRollingWindow()
    {
        var capturedWindows = new List<TimeSpan>();
        var mockAlerts = new Mock<IAlertRepository>();
        mockAlerts.Setup(a => a.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .Callback<TimeSpan>(w => capturedWindows.Add(w))
            .ReturnsAsync(new List<Alert>());
        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.Alerts).Returns(mockAlerts.Object);

        // First call — should use a broader window (~5 minutes from init)
        await _sut.PushNewAlertsAsync(mockUow.Object);

        // Second call — should use narrow window (elapsed since first call)
        await _sut.PushNewAlertsAsync(mockUow.Object);

        capturedWindows.Should().HaveCount(2);
        capturedWindows[1].Should().BeLessThan(capturedWindows[0],
            "second call should use narrow elapsed window");
    }

    [Fact]
    public async Task PushNewAlertsAsync_PrunesPushedAlertIdsAbove1000()
    {
        // Pre-populate >1000 entries in the dedup dictionary
        for (int i = 0; i < 1001; i++)
        {
            _sut.PushedAlertIds.TryAdd(i, 0);
        }
        _sut.PushedAlertIds.Count.Should().Be(1001);

        var newAlert = new Alert { Id = 9999, UserId = "user1", Type = AlertType.SpreadWarning, Severity = AlertSeverity.Warning, Message = "New", CreatedAt = DateTime.UtcNow };
        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.Alerts.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync(new List<Alert> { newAlert });

        await _sut.PushNewAlertsAsync(mockUow.Object);

        // Dictionary should have been cleared and then the new alert added
        _sut.PushedAlertIds.Should().ContainKey(9999);
        _sut.PushedAlertIds.Count.Should().BeLessOrEqualTo(1, "pruning clears all entries, then fresh alerts pass through");

        // The new alert should have been pushed
        GetUserClient("user1").Verify(
            c => c.ReceiveAlert(It.Is<AlertDto>(dto => dto.Id == 9999)),
            Times.Once);
    }

    // ── PushStatusExplanationAsync — Routing (NB4) ──────────────────────────

    [Fact]
    public async Task PushStatusExplanationAsync_WithUserId_TargetsUserGroup()
    {
        await _sut.PushStatusExplanationAsync("user42", "test message", "info");

        GetUserClient("user42").Verify(
            c => c.ReceiveStatusExplanation("test message", "info"),
            Times.Once);
        _mockMarketDataClient.Verify(
            c => c.ReceiveStatusExplanation(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PushStatusExplanationAsync_WithNullUserId_TargetsMarketDataGroup()
    {
        await _sut.PushStatusExplanationAsync(null, "broadcast message", "warning");

        _mockMarketDataClient.Verify(
            c => c.ReceiveStatusExplanation("broadcast message", "warning"),
            Times.Once);
    }

    // ── PushNotificationAsync — Dual-group routing ──────────────────────────

    [Fact]
    public async Task PushNotificationAsync_SendsToBothUserAndAdminGroups()
    {
        await _sut.PushNotificationAsync("user1", "important notification");

        GetUserClient("user1").Verify(
            c => c.ReceiveNotification("important notification"),
            Times.Once);
        _mockAdminsClient.Verify(
            c => c.ReceiveNotification("important notification"),
            Times.Once);
    }

    // ── PushDashboardUpdateAsync — Computation (NB3) ────────────────────────

    [Fact]
    public async Task PushDashboardUpdateAsync_EmptyLists_ProducesZeroValues()
    {
        DashboardDto? captured = null;
        _mockMarketDataClient
            .Setup(c => c.ReceiveDashboardUpdate(It.IsAny<DashboardDto>()))
            .Callback<DashboardDto>(dto => captured = dto)
            .Returns(Task.CompletedTask);

        await _sut.PushDashboardUpdateAsync(
            new List<ArbitragePosition>(),
            new List<ArbitrageOpportunityDto>(),
            botEnabled: true,
            openingCount: 0,
            needsAttentionCount: 0);

        captured.Should().NotBeNull();
        captured!.TotalPnl.Should().Be(0m);
        captured.BestSpread.Should().Be(0m);
        captured.OpenPositionCount.Should().Be(0);
        captured.BotEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task PushDashboardUpdateAsync_SumsAccumulatedFunding()
    {
        DashboardDto? captured = null;
        _mockMarketDataClient
            .Setup(c => c.ReceiveDashboardUpdate(It.IsAny<DashboardDto>()))
            .Callback<DashboardDto>(dto => captured = dto)
            .Returns(Task.CompletedTask);

        var positions = new List<ArbitragePosition>
        {
            new() { AccumulatedFunding = 10.5m },
            new() { AccumulatedFunding = 5.25m },
            new() { AccumulatedFunding = -2.0m },
        };

        var opportunities = new List<ArbitrageOpportunityDto>
        {
            new() { SpreadPerHour = 0.003m },
            new() { SpreadPerHour = 0.007m },
        };

        await _sut.PushDashboardUpdateAsync(positions, opportunities, true, 1, 0);

        captured.Should().NotBeNull();
        captured!.TotalPnl.Should().Be(13.75m);
        captured.BestSpread.Should().Be(0.007m);
        captured.OpenPositionCount.Should().Be(3);
        captured.OpeningPositionCount.Should().Be(1);
    }

    // ── Exception-swallowing tests (NB2) ────────────────────────────────────

    [Fact]
    public async Task PushOpportunityUpdateAsync_SwallowsHubException()
    {
        _mockMarketDataClient
            .Setup(c => c.ReceiveOpportunityUpdate(It.IsAny<OpportunityResultDto>()))
            .ThrowsAsync(new InvalidOperationException("Hub disconnected"));

        var act = () => _sut.PushOpportunityUpdateAsync(new OpportunityResultDto());

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PushNewAlertsAsync_SwallowsHubException()
    {
        var alert = new Alert { Id = 1, UserId = "user1", Type = AlertType.SpreadWarning, Severity = AlertSeverity.Warning, Message = "Test", CreatedAt = DateTime.UtcNow };
        var mockUow = new Mock<IUnitOfWork>();
        mockUow.Setup(u => u.Alerts.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var act = () => _sut.PushNewAlertsAsync(mockUow.Object);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PushStatusExplanationAsync_SwallowsHubException()
    {
        _mockMarketDataClient
            .Setup(c => c.ReceiveStatusExplanation(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Hub disconnected"));

        var act = () => _sut.PushStatusExplanationAsync(null, "test", "info");

        await act.Should().NotThrowAsync();
    }
}
