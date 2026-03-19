using System.Collections.Concurrent;
using FluentAssertions;
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
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class BotOrchestratorTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockSp = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositionRepo = new();
    private readonly Mock<IAlertRepository> _mockAlertRepo = new();
    private readonly Mock<ISignalEngine> _mockSignalEngine = new();
    private readonly Mock<IPositionSizer> _mockPositionSizer = new();
    private readonly Mock<IExecutionEngine> _mockExecutionEngine = new();
    private readonly Mock<IPositionHealthMonitor> _mockHealthMonitor = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext = new();
    private readonly Mock<ILogger<BotOrchestrator>> _mockLogger = new();
    private readonly BotOrchestrator _sut;

    public BotOrchestratorTests()
    {
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockSp.Object);

        _mockSp.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(ISignalEngine))).Returns(_mockSignalEngine.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IPositionSizer))).Returns(_mockPositionSizer.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IExecutionEngine))).Returns(_mockExecutionEngine.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IPositionHealthMonitor))).Returns(_mockHealthMonitor.Object);

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlertRepo.Object);

        _mockAlertRepo.Setup(r => r.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync([]);

        // Stub SignalR hub context
        var mockClients = new Mock<IHubClients<IDashboardClient>>();
        var mockClient = new Mock<IDashboardClient>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClient.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        _sut = new BotOrchestrator(
            _mockScopeFactory.Object,
            _mockHubContext.Object,
            _mockLogger.Object);
    }

    private static ArbitrageOpportunityDto MakeOpp(int assetId, string symbol, int longExId = 1, int shortExId = 2) =>
        new()
        {
            AssetId = assetId,
            AssetSymbol = symbol,
            LongExchangeId = longExId,
            ShortExchangeId = shortExId,
            LongExchangeName = "ExA",
            ShortExchangeName = "ExB",
            NetYieldPerHour = 0.001m,
            SpreadPerHour = 0.001m,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };

    private BotConfiguration DefaultConfig(
        AllocationStrategy strategy = AllocationStrategy.Concentrated,
        int topN = 3,
        int maxPositions = 5) => new()
    {
        IsEnabled = true,
        OpenThreshold = 0.0003m,
        MaxConcurrentPositions = maxPositions,
        AllocationStrategy = strategy,
        AllocationTopN = topN,
        TotalCapitalUsdc = 1000m,
        MaxCapitalPerPosition = 0.5m,
        VolumeFraction = 0.001m,
        UpdatedByUserId = "test-user",
    };

    [Fact]
    public async Task RunCycle_OnGenericError_ContinuesToNextOpportunity()
    {
        var config = DefaultConfig(AllocationStrategy.EqualSpread, topN: 2, maxPositions: 5);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([opp1, opp2]);

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>()))
            .ReturnsAsync([100m, 100m]);

        // First opp fails with generic error, second succeeds
        _mockExecutionEngine.SetupSequence(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange timeout"))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both were attempted
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunCycle_OnBalanceError_StopsIterating()
    {
        var config = DefaultConfig(AllocationStrategy.EqualSpread, topN: 2, maxPositions: 5);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([opp1, opp2]);

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>()))
            .ReturnsAsync([100m, 100m]);

        // First opp fails with balance error
        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin for order"));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Only first was attempted — balance error stops iteration
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void ClearCooldowns_EmptiesDictionary()
    {
        _sut.FailedOpCooldowns["test_key"] = (DateTime.UtcNow.AddMinutes(30), 3);

        _sut.ClearCooldowns();

        _sut.FailedOpCooldowns.Should().BeEmpty();
    }

    [Fact]
    public async Task RunCycle_EqualSpread_OpensBothPositions()
    {
        var config = DefaultConfig(AllocationStrategy.EqualSpread, topN: 2, maxPositions: 5);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([opp1, opp2]);

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>()))
            .ReturnsAsync([250m, 250m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both positions should have been opened
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
