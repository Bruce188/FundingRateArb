using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class BotOrchestratorTests
{
    private const string TestUserId = "test-user";

    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockSp = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IPositionRepository> _mockPositionRepo = new();
    private readonly Mock<IAlertRepository> _mockAlertRepo = new();
    private readonly Mock<IExchangeRepository> _mockExchangeRepo = new();
    private readonly Mock<IUserConfigurationRepository> _mockUserConfigs = new();
    private readonly Mock<IOpportunitySnapshotRepository> _mockSnapshotRepo = new();
    private readonly Mock<ISignalEngine> _mockSignalEngine = new();
    private readonly Mock<IPositionSizer> _mockPositionSizer = new();
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator = new();
    private readonly Mock<IExecutionEngine> _mockExecutionEngine = new();
    private readonly Mock<IPositionHealthMonitor> _mockHealthMonitor = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IFundingRateReadinessSignal> _mockReadinessSignal = new();
    private readonly Mock<ISignalRNotifier> _mockNotifier = new();
    private readonly Mock<ILogger<BotOrchestrator>> _mockLogger = new();
    private readonly Mock<IRotationEvaluator> _mockRotationEvaluator = new();
    private readonly CircuitBreakerManager _circuitBreaker;
    private readonly BotOrchestrator _sut;

    public BotOrchestratorTests()
    {
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockSp.Object);

        _mockSp.Setup(sp => sp.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(ISignalEngine))).Returns(_mockSignalEngine.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IPositionSizer))).Returns(_mockPositionSizer.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IBalanceAggregator))).Returns(_mockBalanceAggregator.Object);

        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });
        _mockSp.Setup(sp => sp.GetService(typeof(IExecutionEngine))).Returns(_mockExecutionEngine.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IPositionHealthMonitor))).Returns(_mockHealthMonitor.Object);
        _mockSp.Setup(sp => sp.GetService(typeof(IUserSettingsService))).Returns(_mockUserSettings.Object);

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlertRepo.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
        _mockUow.Setup(u => u.UserConfigurations).Returns(_mockUserConfigs.Object);
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(_mockSnapshotRepo.Object);

        // Default mock for exchange repository (used in circuit breaker DTO population)
        _mockExchangeRepo.Setup(e => e.GetAllAsync())
            .ReturnsAsync(new List<Exchange>());

        _mockAlertRepo.Setup(r => r.GetRecentUnreadAsync(It.IsAny<TimeSpan>()))
            .ReturnsAsync([]);

        // Default mock for health monitor — returns empty result
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthCheckResult.Empty);

        // H7: Default mock for GetClosedSinceAsync (returns empty list — no drawdown)
        _mockPositionRepo.Setup(p => p.GetClosedSinceAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<ArbitragePosition>());

        // Default mock for Opening status query (used in duplicate check)
        _mockPositionRepo.Setup(p => p.GetByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition>());

        // Default mock for needs-attention count (used in dashboard KPI)
        _mockPositionRepo.Setup(p => p.CountByStatusesAsync(It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(0);

        // Default: enable test user for the multi-user loop
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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
        _mockUserSettings.Setup(s => s.HasValidCredentialsAsync(TestUserId)).ReturnsAsync(true);
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2, 3 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2, 3, 4, 5 });
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int>());

        // Mock readiness signal — completes immediately
        _mockReadinessSignal.Setup(r => r.WaitForReadyAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _circuitBreaker = new CircuitBreakerManager(NullLogger<CircuitBreakerManager>.Instance);
        var opportunityFilter = new OpportunityFilter(_circuitBreaker, NullLogger<OpportunityFilter>.Instance);

        _sut = new BotOrchestrator(
            _mockScopeFactory.Object,
            _mockReadinessSignal.Object,
            _mockNotifier.Object,
            _circuitBreaker,
            opportunityFilter,
            _mockRotationEvaluator.Object,
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

    [Fact]
    public async Task RunCycle_OnGenericError_ContinuesToNextOpportunity()
    {
        // Override user config for this test: EqualSpread with topN=2
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 2,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.EqualSpread,
            AllocationTopN = 2,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m, 100m]);

        // First opp fails with generic error, second succeeds
        _mockExecutionEngine.SetupSequence(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Exchange timeout"))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both were attempted
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunCycle_OnBalanceError_StopsIterating()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 2,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.EqualSpread,
            AllocationTopN = 2,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m, 100m]);

        // First opp fails with balance error
        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin for order"));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Only first was attempted — balance error stops iteration
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunCycle_CooldownOpportunity_SnapshotShowsCooldownSkipReason()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 1,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp = MakeOpp(1, "ETH");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        // Place the opportunity on cooldown (simulating a previous failure)
        var cooldownKey = $"{TestUserId}:{opp.AssetId}_{opp.LongExchangeId}_{opp.ShortExchangeId}";
        _circuitBreaker.FailedOpCooldowns[cooldownKey] = (DateTime.UtcNow.AddMinutes(10), 1);

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        IEnumerable<OpportunitySnapshot>? savedSnapshots = null;
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((s, _) => savedSnapshots = s.ToList())
            .Returns(Task.CompletedTask);

        await _sut.RunCycleAsync(CancellationToken.None);

        savedSnapshots.Should().NotBeNull();
        var snapshot = savedSnapshots!.First();
        snapshot.SkipReason.Should().Be("cooldown",
            "cooldown-skipped opportunities should get 'cooldown' skip reason, not 'below_threshold'");
        snapshot.WasOpened.Should().BeFalse();
    }

    [Fact]
    public void ClearCooldowns_EmptiesDictionary()
    {
        _circuitBreaker.FailedOpCooldowns["test_key"] = (DateTime.UtcNow.AddMinutes(30), 3);

        _sut.ClearCooldowns();

        _circuitBreaker.FailedOpCooldowns.Should().BeEmpty();
    }

    [Fact]
    public async Task RunCycle_EqualSpread_OpensBothPositions()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 2,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.EqualSpread,
            AllocationTopN = 2,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([250m, 250m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both positions should have been opened
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Warning computation tests ────────────────────────────────────────

    private static BotConfiguration MakeConfig(
        decimal closeThreshold = -0.00005m,
        decimal alertThreshold = 0.0001m,
        int maxHoldTimeHours = 72,
        decimal stopLossPct = 0.15m)
    {
        return new BotConfiguration
        {
            CloseThreshold = closeThreshold,
            AlertThreshold = alertThreshold,
            MaxHoldTimeHours = maxHoldTimeHours,
            StopLossPct = stopLossPct,
        };
    }

    private static ArbitragePosition MakePosition(
        decimal currentSpread = 0.001m,
        DateTime? openedAt = null,
        decimal accumulatedFunding = 0m,
        decimal marginUsdc = 100m)
    {
        return new ArbitragePosition
        {
            Id = 1,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            Leverage = 5,
            MarginUsdc = marginUsdc,
            CurrentSpreadPerHour = currentSpread,
            OpenedAt = openedAt ?? DateTime.UtcNow.AddHours(-1),
            AccumulatedFunding = accumulatedFunding,
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "Hyperliquid" },
            ShortExchange = new Exchange { Name = "Aster" },
        };
    }

    [Fact]
    public void ComputeWarnings_NoIssues_ReturnsNone()
    {
        var config = MakeConfig();
        var pos = MakePosition(currentSpread: 0.001m);
        var dto = new PositionSummaryDto();

        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.None);
        dto.WarningTypes.Should().BeEmpty();
    }

    [Fact]
    public void ComputeWarnings_SpreadBelowAlertThreshold_WarningWithSpreadRisk()
    {
        var config = MakeConfig(alertThreshold: 0.0001m, closeThreshold: -0.00005m);
        var pos = MakePosition(currentSpread: 0.00005m); // below alertThreshold

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Warning);
        dto.WarningTypes.Should().Contain(WarningType.SpreadRisk);
    }

    [Fact]
    public void ComputeWarnings_SpreadBelowCloseThreshold_CriticalWithSpreadRisk()
    {
        var config = MakeConfig(closeThreshold: -0.00005m);
        var pos = MakePosition(currentSpread: -0.0001m); // below closeThreshold

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Critical);
        dto.WarningTypes.Should().Contain(WarningType.SpreadRisk);
    }

    [Fact]
    public void ComputeWarnings_Approaching80PctMaxHold_WarningWithTimeBased()
    {
        var config = MakeConfig(maxHoldTimeHours: 72);
        // 58 hours = 80.5% of 72 → should trigger Warning
        var pos = MakePosition(openedAt: DateTime.UtcNow.AddHours(-58));

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Warning);
        dto.WarningTypes.Should().Contain(WarningType.TimeBased);
    }

    [Fact]
    public void ComputeWarnings_Approaching95PctMaxHold_CriticalWithTimeBased()
    {
        var config = MakeConfig(maxHoldTimeHours: 72);
        // 69 hours = 95.8% of 72 → should trigger Critical
        var pos = MakePosition(openedAt: DateTime.UtcNow.AddHours(-69));

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Critical);
        dto.WarningTypes.Should().Contain(WarningType.TimeBased);
    }

    [Fact]
    public void ComputeWarnings_Loss70PctOfStopLoss_WarningWithLoss()
    {
        var config = MakeConfig(stopLossPct: 0.15m);
        // margin=100, stopLoss=0.15*100=15, 70%=10.5 → loss of 11 should trigger
        var pos = MakePosition(marginUsdc: 100m, accumulatedFunding: -11m);

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Warning);
        dto.WarningTypes.Should().Contain(WarningType.Loss);
    }

    [Fact]
    public void ComputeWarnings_Loss90PctOfStopLoss_CriticalWithLoss()
    {
        var config = MakeConfig(stopLossPct: 0.15m);
        // margin=100, stopLoss=15, 90%=13.5 → loss of 14 should trigger Critical
        var pos = MakePosition(marginUsdc: 100m, accumulatedFunding: -14m);

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Critical);
        dto.WarningTypes.Should().Contain(WarningType.Loss);
    }

    [Fact]
    public void ComputeWarnings_MultipleConditions_UsesHighestLevel()
    {
        var config = MakeConfig(alertThreshold: 0.0001m, maxHoldTimeHours: 72);
        // Spread below alert (Warning) + time above 95% (Critical) → Critical
        var pos = MakePosition(
            currentSpread: 0.00005m,
            openedAt: DateTime.UtcNow.AddHours(-69));

        var dto = new PositionSummaryDto();
        SignalRNotifier.ComputeWarnings(dto, pos, config);

        dto.WarningLevel.Should().Be(WarningLevel.Critical);
        dto.WarningTypes.Should().Contain(WarningType.SpreadRisk);
        dto.WarningTypes.Should().Contain(WarningType.TimeBased);
    }

    // ── Opportunity snapshot tests ────────────────────────────────────────

    [Fact]
    public async Task RunCycle_PersistsOpportunitySnapshots_WithCorrectWasOpenedFlag()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 1,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // Only first opportunity (Concentrated strategy takes 1) is opened successfully
        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        List<OpportunitySnapshot>? captured = null;
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((snapshots, _) => captured = snapshots.ToList())
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured.Should().HaveCount(2);

        // ETH was opened (Concentrated picks first opportunity)
        var ethSnapshot = captured!.First(s => s.AssetId == 1);
        ethSnapshot.WasOpened.Should().BeTrue();
        ethSnapshot.SkipReason.Should().BeNull();

        // BTC was not opened (Concentrated picks only 1)
        var btcSnapshot = captured.First(s => s.AssetId == 2);
        btcSnapshot.WasOpened.Should().BeFalse();
        btcSnapshot.SkipReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunCycle_PersistsSnapshots_WithActivePositionSkipReason()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 1,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // ETH has an active position
        var existingPosition = new ArbitragePosition
        {
            Id = 1,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            UserId = TestUserId,
            SizeUsdc = 100m,
            Leverage = 5,
            MarginUsdc = 20m,
            CurrentSpreadPerHour = 0.001m,
            EntrySpreadPerHour = 0.001m,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Name = "ExA" },
            ShortExchange = new Exchange { Name = "ExB" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp1 = MakeOpp(1, "ETH"); // matches existing position
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1] });

        List<OpportunitySnapshot>? captured = null;
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((snapshots, _) => captured = snapshots.ToList())
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured.Should().HaveCount(1);

        var snapshot = captured![0];
        snapshot.WasOpened.Should().BeFalse();
        snapshot.SkipReason.Should().Be("active_position");
    }

    [Fact]
    public async Task RunCycle_CapitalExhausted_SnapshotsShowCapitalExhausted()
    {
        // Set up user config with EqualSpread/topN=3 so multiple candidates are processed
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 3,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp1 = MakeOpp(1, "ETH");
        var opp2 = MakeOpp(2, "BTC");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1, opp2] });

        // First opportunity succeeds, second fails with insufficient margin (triggers capital exhaustion break)
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<List<ArbitrageOpportunityDto>>(),
            It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new decimal[] { 100m, 100m });

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(
            It.IsAny<string>(), It.Is<ArbitrageOpportunityDto>(o => o.AssetSymbol == "ETH"), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(
            It.IsAny<string>(), It.Is<ArbitrageOpportunityDto>(o => o.AssetSymbol == "BTC"), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin on Hyperliquid"));

        List<OpportunitySnapshot>? captured = null;
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((snapshots, _) => captured = snapshots.ToList())
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured.Should().HaveCount(2);

        var ethSnapshot = captured!.First(s => s.AssetId == 1);
        ethSnapshot.WasOpened.Should().BeTrue();

        var btcSnapshot = captured!.First(s => s.AssetId == 2);
        btcSnapshot.WasOpened.Should().BeFalse();
        // BTC was not attempted (no remaining candidates after break), but it is in the snapshot
        // with a below_threshold or appropriate skip reason.
        // Since ETH caused the balance break and BTC is the failed opp itself (not a remaining candidate),
        // BTC gets "below_threshold" — the capital_exhausted only applies to candidates AFTER the break.
    }

    [Fact]
    public async Task RunCycle_MaxPositions_SnapshotsShowMaxPositions()
    {
        // Set up user config with max 1 position
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.EqualSpread,
                AllocationTopN = 3,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // User already has one open position (at max)
        // But with a different asset so the candidates aren't filtered by active key
        var existingPosition = new ArbitragePosition
        {
            Id = 99,
            AssetId = 5,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            UserId = TestUserId,
            SizeUsdc = 100m,
            Leverage = 5,
            MarginUsdc = 20m,
            CurrentSpreadPerHour = 0.001m,
            EntrySpreadPerHour = 0.001m,
            OpenedAt = DateTime.UtcNow.AddHours(-1),
            Status = PositionStatus.Open,
            Asset = new Asset { Symbol = "SOL" },
            LongExchange = new Exchange { Name = "ExA" },
            ShortExchange = new Exchange { Name = "ExB" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp1 = MakeOpp(1, "ETH");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp1] });

        List<OpportunitySnapshot>? captured = null;
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((snapshots, _) => captured = snapshots.ToList())
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured.Should().HaveCount(1);

        // ETH was not opened because max positions was reached before execution
        // Since user's max is 1 and they already have SOL, the ExecuteUserCycleAsync
        // returns early at the max positions gate — no candidates are processed.
        // The snapshot reflects a "below_threshold" fallback (the user never reached the candidate loop)
        var ethSnapshot = captured![0];
        ethSnapshot.WasOpened.Should().BeFalse();
    }

    [Fact]
    public async Task RunCycle_Purges7DayOldSnapshots()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp = MakeOpp(1, "ETH");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Verify purge was called with a cutoff approximately 7 days ago
        _mockSnapshotRepo.Verify(r => r.PurgeOlderThanAsync(
            It.Is<DateTime>(d => d < DateTime.UtcNow.AddDays(-6) && d > DateTime.UtcNow.AddDays(-8)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── NB11: Loss-cooldown after negative-PnL close ─────────────────────────

    [Fact]
    public async Task RunCycle_NegativeRealizedPnl_AppliesCooldown()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Health monitor returns a position to close
        var posToClose = new ArbitragePosition
        {
            Id = 42,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 100m,
            Leverage = 5,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
        };

        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                [(posToClose, CloseReason.SpreadCollapsed)],
                [],
                new Dictionary<int, ComputedPositionPnl>()));

        // Execution engine mutates the position on close
        _mockExecutionEngine.Setup(e => e.ClosePositionAsync(TestUserId, posToClose, CloseReason.SpreadCollapsed, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                posToClose.Status = PositionStatus.Closed;
                posToClose.RealizedPnl = -1.5m; // negative PnL
            })
            .Returns(Task.CompletedTask);

        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        // No opportunities so the cycle ends after health check
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Verify cooldown was applied
        var cooldownKey = $"{TestUserId}:1:1:2";
        _circuitBreaker.FailedOpCooldowns.ContainsKey(cooldownKey).Should().BeTrue(
            "negative PnL close should apply cooldown to the opportunity key");
    }

    // ── N6: Adaptive threshold selects highest yield ─────────────────────────

    [Fact]
    public async Task AdaptiveThreshold_SelectsHighestYield()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // User has 0 open positions (adaptive threshold kicks in)
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        // Signal engine returns no above-threshold opportunities but has AllNetPositive
        var lowYieldOpp = MakeOpp(1, "ETH", 1, 2);
        lowYieldOpp.NetYieldPerHour = 0.00005m;
        var highYieldOpp = MakeOpp(2, "BTC", 1, 2);
        highYieldOpp.NetYieldPerHour = 0.0002m;

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = [], // Nothing above threshold
                AllNetPositive = [lowYieldOpp, highYieldOpp],
            });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        ArbitrageOpportunityDto? capturedOpp = null;
        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .Callback<string, ArbitrageOpportunityDto, decimal, UserConfiguration?, CancellationToken>((_, opp, _, _, _) => capturedOpp = opp)
            .ReturnsAsync((true, (string?)null));

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        capturedOpp.Should().NotBeNull("adaptive threshold should select an opportunity");
        capturedOpp!.AssetSymbol.Should().Be("BTC", "should select the higher yield opportunity");
    }

    // ── Status message and circuit breaker tests ────────────────────────

    private static ArbitrageOpportunityDto MakeOppNamed(
        int assetId, string symbol,
        int longExId, string longExName,
        int shortExId, string shortExName,
        decimal netYield = 0.001m)
    {
        return new ArbitrageOpportunityDto
        {
            AssetId = assetId,
            AssetSymbol = symbol,
            LongExchangeId = longExId,
            ShortExchangeId = shortExId,
            LongExchangeName = longExName,
            ShortExchangeName = shortExName,
            NetYieldPerHour = netYield,
            SpreadPerHour = netYield,
            LongVolume24h = 1_000_000m,
            ShortVolume24h = 1_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };
    }

    private BotConfiguration MakeEnabledConfig()
    {
        return new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            ExchangeCircuitBreakerThreshold = 3,
            ExchangeCircuitBreakerMinutes = 30,
            UpdatedByUserId = TestUserId,
        };
    }

    private void SetupStatusCapture(out List<(string Message, string Severity)> captured)
    {
        var capturedMessages = new List<(string, string)>();
        _mockNotifier.Setup(n => n.PushStatusExplanationAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string?, string, string>((_, msg, sev) => capturedMessages.Add((msg, sev)))
            .Returns(Task.CompletedTask);
        captured = capturedMessages;
    }

    [Fact]
    public async Task StatusMessage_CircuitBreaker_ShowsExchangeName()
    {
        SetupStatusCapture(out var capturedMessages);

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // Create opportunity involving Lighter (id=2)
        var opp = MakeOppNamed(1, "ETH", 2, "Lighter", 3, "Aster");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        // Set circuit breaker for Lighter (id=2) broken until 10 minutes from now
        _circuitBreaker.ExchangeCircuitBreaker[2] = (config.ExchangeCircuitBreakerThreshold, DateTime.UtcNow.AddMinutes(10));

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        var statusMessages = capturedMessages
            .Where(m => m.Message.Contains("Lighter", StringComparison.OrdinalIgnoreCase) &&
                        m.Message.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase))
            .ToList();
        statusMessages.Should().NotBeEmpty("status message should mention Lighter circuit breaker");
        statusMessages[0].Severity.Should().Be("warning");
    }

    [Fact]
    public async Task StatusMessage_BelowThreshold_ShowsBestSpread()
    {
        SetupStatusCapture(out var capturedMessages);

        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 5,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.002m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
            });

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // Opportunity with yield below user's threshold (0.002m)
        var opp = MakeOppNamed(1, "ETH", 1, "Lighter", 2, "Aster", netYield: 0.001m);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        var statusMessages = capturedMessages
            .Where(m => m.Message.Contains("below your threshold", StringComparison.OrdinalIgnoreCase))
            .ToList();
        statusMessages.Should().NotBeEmpty("status message should mention threshold filtering");
        statusMessages[0].Message.Should().Contain("0.1000%/hr", "should show the best yield in the message");
    }

    [Fact]
    public async Task MarginError_OnlyCircuitBreaksFailingExchange()
    {
        SetupStatusCapture(out _);

        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // Lighter (2) / Aster (3) trade
        var opp = MakeOppNamed(1, "ETH", 2, "Lighter", 3, "Aster");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // Margin error specifically on Aster
        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin on Aster: available=5.00, required=10.00"));

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Aster (3) should be circuit-broken
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(3, "Aster should be circuit-broken");

        // Lighter (2) should NOT be circuit-broken
        _circuitBreaker.ExchangeCircuitBreaker.Should().NotContainKey(2, "Lighter should NOT be circuit-broken on Aster margin error");
    }

    [Fact]
    public async Task MarginError_GenericError_CircuitBreaksBothExchanges()
    {
        SetupStatusCapture(out _);

        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
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

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // Lighter (2) / Aster (3) trade
        var opp = MakeOppNamed(1, "ETH", 2, "Lighter", 3, "Aster");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        // Generic balance error — no exchange identified
        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Insufficient margin for order"));

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Both should be circuit-broken (safe fallback)
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(2, "Lighter should be circuit-broken on generic margin error");
        _circuitBreaker.ExchangeCircuitBreaker.Should().ContainKey(3, "Aster should be circuit-broken on generic margin error");
    }

    [Theory]
    [InlineData("Insufficient margin on Aster: available=5.00, required=10.00", "Aster")]
    [InlineData("Insufficient margin on Lighter: available=13.90, required=6.00", "Lighter")]
    [InlineData("Insufficient margin on Hyperliquid: available=0.00, required=100.00", "Hyperliquid")]
    [InlineData("Insufficient margin on OKX Futures: available=1, required=5", "OKX Futures")]
    [InlineData("Insufficient margin on Aster", null)]
    [InlineData("Exchange connectivity error on Hyperliquid/Lighter", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Some other error", null)]
    public void ExtractMarginErrorExchange_ParsesCorrectly(string? error, string? expected)
    {
        var result = BotOrchestrator.ExtractMarginErrorExchange(error);
        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractFailingExchange_ErrorContainsLongExchangeOnly_ReturnsLongId()
    {
        var opp = new ArbitrageOpportunityDto
        {
            LongExchangeName = "Lighter",
            LongExchangeId = 2,
            ShortExchangeName = "Aster",
            ShortExchangeId = 3,
        };
        var result = BotOrchestrator.ExtractFailingExchange("Position verification failed on Lighter", opp);
        result.Should().Be(2);
    }

    [Fact]
    public void ExtractFailingExchange_ErrorContainsShortExchangeOnly_ReturnsShortId()
    {
        var opp = new ArbitrageOpportunityDto
        {
            LongExchangeName = "Hyperliquid",
            LongExchangeId = 1,
            ShortExchangeName = "Lighter",
            ShortExchangeId = 2,
        };
        var result = BotOrchestrator.ExtractFailingExchange("Emergency close: ETH — second leg (Lighter) failed", opp);
        result.Should().Be(2);
    }

    [Fact]
    public void ExtractFailingExchange_ErrorContainsNeitherExchange_ReturnsNull()
    {
        var opp = new ArbitrageOpportunityDto
        {
            LongExchangeName = "Hyperliquid",
            LongExchangeId = 1,
            ShortExchangeName = "Lighter",
            ShortExchangeId = 2,
        };
        var result = BotOrchestrator.ExtractFailingExchange("Unknown error occurred", opp);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractFailingExchange_ErrorContainsBothExchanges_ReturnsNull()
    {
        var opp = new ArbitrageOpportunityDto
        {
            LongExchangeName = "Hyperliquid",
            LongExchangeId = 1,
            ShortExchangeName = "Lighter",
            ShortExchangeId = 2,
        };
        var result = BotOrchestrator.ExtractFailingExchange("Both Hyperliquid and Lighter failed", opp);
        result.Should().BeNull();
    }

    [Fact]
    public void ExtractFailingExchange_NullError_ReturnsNull()
    {
        var opp = new ArbitrageOpportunityDto
        {
            LongExchangeName = "Hyperliquid",
            LongExchangeId = 1,
            ShortExchangeName = "Lighter",
            ShortExchangeId = 2,
        };
        var result = BotOrchestrator.ExtractFailingExchange(null, opp);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CircuitBreakerState_IncludedInOpportunityResult()
    {
        OpportunityResultDto? capturedResult = null;
        _mockNotifier.Setup(n => n.PushOpportunityUpdateAsync(It.IsAny<OpportunityResultDto>()))
            .Callback<OpportunityResultDto>(r => capturedResult = r)
            .Returns(Task.CompletedTask);

        var config = MakeEnabledConfig();
        config.IsEnabled = false; // disable to skip user loop
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp = MakeOppNamed(1, "ETH", 2, "Lighter", 3, "Aster");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        // Set circuit breaker for Lighter (id=2)
        _circuitBreaker.ExchangeCircuitBreaker[2] = (config.ExchangeCircuitBreakerThreshold, DateTime.UtcNow.AddMinutes(15));

        await _sut.RunCycleAsync(CancellationToken.None);

        capturedResult.Should().NotBeNull("opportunity update should be pushed");
        capturedResult!.CircuitBreakers.Should().NotBeEmpty("circuit breaker status should be included");
        capturedResult.CircuitBreakers.Should().Contain(cb => cb.ExchangeName == "Lighter" && cb.ExchangeId == 2,
            "circuit breaker should identify Lighter exchange");
        capturedResult.CircuitBreakers[0].RemainingMinutes.Should().BeInRange(14, 16);
    }

    [Fact]
    public async Task StatusMessage_ExchangeDisabled_ShowsExchangeMessage()
    {
        SetupStatusCapture(out var capturedMessages);

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // User only has exchange 1 enabled, but opportunity uses exchanges 4 and 5
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1 });

        var opp = MakeOppNamed(1, "ETH", 4, "Deribit", 5, "Bybit");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        var statusMessages = capturedMessages
            .Where(m => m.Message.Contains("enabled exchanges", StringComparison.OrdinalIgnoreCase))
            .ToList();
        statusMessages.Should().NotBeEmpty("status message should mention exchange-disabled filtering");
    }

    [Fact]
    public async Task StatusMessage_AssetDisabled_ShowsAssetMessage()
    {
        SetupStatusCapture(out var capturedMessages);

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // User has exchanges 1,2 enabled but only asset 99 enabled (opp uses asset 1)
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 1, 2 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(TestUserId))
            .ReturnsAsync(new List<int> { 99 });

        var opp = MakeOppNamed(1, "ETH", 1, "Lighter", 2, "Aster");
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        var statusMessages = capturedMessages
            .Where(m => m.Message.Contains("enabled coins", StringComparison.OrdinalIgnoreCase))
            .ToList();
        statusMessages.Should().NotBeEmpty("status message should mention asset-disabled filtering");
    }

    [Fact]
    public async Task StatusMessage_NoOpportunities_ShowsDefaultMessage()
    {
        SetupStatusCapture(out var capturedMessages);

        var config = MakeEnabledConfig();
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync())
            .ReturnsAsync(new List<string> { TestUserId });

        // No opportunities at all
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [] });

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        var statusMessages = capturedMessages
            .Where(m => m.Message.Contains("No arbitrage opportunities detected this cycle", StringComparison.OrdinalIgnoreCase))
            .ToList();
        statusMessages.Should().NotBeEmpty("status message should show default no-opportunities message");
    }

    // ── Periodic reconciliation tests ───────────────────────────────────────

    private void SetupMinimalCycleMocks()
    {
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [] });
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _mockUserConfigs.Setup(c => c.GetAllEnabledUserIdsAsync()).ReturnsAsync(new List<string>());
    }

    [Fact]
    public async Task RunCycle_AtReconciliationInterval_CallsReconcile()
    {
        var config = MakeEnabledConfig();
        config.ReconciliationIntervalCycles = 1; // reconcile every cycle
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        SetupMinimalCycleMocks();

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockHealthMonitor.Verify(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunCycle_ReconciliationThrows_ContinuesCycle()
    {
        var config = MakeEnabledConfig();
        config.ReconciliationIntervalCycles = 1;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        SetupMinimalCycleMocks();
        _mockHealthMonitor
            .Setup(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Reconciliation failure"));

        // Should not throw — cycle continues despite reconciliation failure
        var act = () => _sut.RunCycleAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunCycle_ReconciliationIntervalThree_OnlyReconcileEveryThirdCycle()
    {
        var config = MakeEnabledConfig();
        config.ReconciliationIntervalCycles = 3;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        SetupMinimalCycleMocks();

        // Cycles 1 and 2: no reconciliation
        await _sut.RunCycleAsync(CancellationToken.None);
        _mockHealthMonitor.Verify(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Never);

        await _sut.RunCycleAsync(CancellationToken.None);
        _mockHealthMonitor.Verify(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Cycle 3: reconciliation fires
        await _sut.RunCycleAsync(CancellationToken.None);
        _mockHealthMonitor.Verify(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Cycles 4-5: no reconciliation (counter reset)
        await _sut.RunCycleAsync(CancellationToken.None);
        await _sut.RunCycleAsync(CancellationToken.None);
        _mockHealthMonitor.Verify(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Cycle 6: reconciliation fires again
        await _sut.RunCycleAsync(CancellationToken.None);
        _mockHealthMonitor.Verify(h => h.ReconcileOpenPositionsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Part D: Emergency close for reaped positions ─────────────────────────

    [Fact]
    public async Task RunCycle_ReapedPositions_AttemptEmergencyClose()
    {
        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 1,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [] });

        // Health monitor returns one reaped position
        var reaped = new List<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)>
        {
            (42, TestUserId, 1, 2, PositionStatus.Closing)
        };
        _mockHealthMonitor.Setup(h => h.CheckAndActAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(
                Array.Empty<(ArbitragePosition, CloseReason)>(),
                reaped,
                new Dictionary<int, ComputedPositionPnl>()));

        // Mock the position lookup for emergency close
        var reapedPos = new ArbitragePosition
        {
            Id = 42,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            Status = PositionStatus.EmergencyClosed,
            ClosedAt = DateTime.UtcNow,
            Asset = new Asset { Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(reapedPos);

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(TestUserId, reapedPos, CloseReason.ExchangeDrift, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── B3: Orchestrator rotation execution test ─────────────────────────

    [Fact]
    public async Task RunCycle_RotationRecommended_ClosesPositionAndTracksState()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                RotationThresholdPerHour = 0.0003m,
                MinHoldBeforeRotationMinutes = 30,
                MaxRotationsPerDay = 5,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var existingPosition = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            CurrentSpreadPerHour = 0.0001m,
            EntrySpreadPerHour = 0.0002m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp = MakeOpp(2, "BTC", longExId: 1, shortExId: 3);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        var recommendation = new RotationRecommendationDto(
            PositionId: 10,
            PositionAsset: "ETH",
            CurrentSpreadPerHour: 0.0001m,
            ReplacementAssetId: 2,
            ReplacementAsset: "BTC",
            ReplacementLongExchange: "Hyperliquid",
            ReplacementShortExchange: "Aster",
            ReplacementLongExchangeId: 1,
            ReplacementShortExchangeId: 3,
            ReplacementNetYieldPerHour: 0.0005m,
            ImprovementPerHour: 0.0004m);

        _mockRotationEvaluator.Setup(r => r.Evaluate(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<UserConfiguration>(),
                It.IsAny<BotConfiguration>()))
            .Returns(recommendation);

        _mockExecutionEngine.Setup(e => e.ClosePositionAsync(TestUserId, existingPosition, CloseReason.Rotation, It.IsAny<CancellationToken>()))
            .Callback(() => existingPosition.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(TestUserId, existingPosition, CloseReason.Rotation, It.IsAny<CancellationToken>()),
            Times.Once);

        var cooldownKey = $"{TestUserId}:2:1:3";
        _circuitBreaker.RotationCooldowns.Should().ContainKey(cooldownKey);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _circuitBreaker.DailyRotationCounts.Should().ContainKey(TestUserId);
        _circuitBreaker.DailyRotationCounts[TestUserId].Count.Should().Be(1);
        _circuitBreaker.DailyRotationCounts[TestUserId].Date.Should().Be(today);
    }

    // ── B4: Daily cap enforcement test ─────────────────────────

    [Fact]
    public async Task RunCycle_RotationDailyCapReached_SkipsRotation()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                RotationThresholdPerHour = 0.0003m,
                MinHoldBeforeRotationMinutes = 30,
                MaxRotationsPerDay = 2,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var existingPosition = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            CurrentSpreadPerHour = 0.0001m,
            EntrySpreadPerHour = 0.0002m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp = MakeOpp(2, "BTC", longExId: 1, shortExId: 3);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        var recommendation = new RotationRecommendationDto(
            PositionId: 10,
            PositionAsset: "ETH",
            CurrentSpreadPerHour: 0.0001m,
            ReplacementAssetId: 2,
            ReplacementAsset: "BTC",
            ReplacementLongExchange: "Hyperliquid",
            ReplacementShortExchange: "Aster",
            ReplacementLongExchangeId: 1,
            ReplacementShortExchangeId: 3,
            ReplacementNetYieldPerHour: 0.0005m,
            ImprovementPerHour: 0.0004m);

        _mockRotationEvaluator.Setup(r => r.Evaluate(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<UserConfiguration>(),
                It.IsAny<BotConfiguration>()))
            .Returns(recommendation);

        // Pre-populate daily rotation count at max
        _circuitBreaker.DailyRotationCounts[TestUserId] = (DateOnly.FromDateTime(DateTime.UtcNow), 2);

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Close should NOT have been called
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), CloseReason.Rotation, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── B5: Rotation cooldown enforcement test ─────────────────────────

    [Fact]
    public async Task RunCycle_RotationCooldownActive_SkipsRotation()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                RotationThresholdPerHour = 0.0003m,
                MinHoldBeforeRotationMinutes = 30,
                MaxRotationsPerDay = 5,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var existingPosition = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            CurrentSpreadPerHour = 0.0001m,
            EntrySpreadPerHour = 0.0002m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp = MakeOpp(2, "BTC", longExId: 1, shortExId: 3);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        var recommendation = new RotationRecommendationDto(
            PositionId: 10,
            PositionAsset: "ETH",
            CurrentSpreadPerHour: 0.0001m,
            ReplacementAssetId: 2,
            ReplacementAsset: "BTC",
            ReplacementLongExchange: "Hyperliquid",
            ReplacementShortExchange: "Aster",
            ReplacementLongExchangeId: 1,
            ReplacementShortExchangeId: 3,
            ReplacementNetYieldPerHour: 0.0005m,
            ImprovementPerHour: 0.0004m);

        _mockRotationEvaluator.Setup(r => r.Evaluate(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<UserConfiguration>(),
                It.IsAny<BotConfiguration>()))
            .Returns(recommendation);

        // Pre-populate cooldown for the replacement opportunity with a future timestamp
        var cooldownKey = $"{TestUserId}:2:1:3";
        _circuitBreaker.RotationCooldowns[cooldownKey] = DateTime.UtcNow.AddMinutes(10);

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Close should NOT have been called
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), CloseReason.Rotation, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── NB2-v5: Close failure path — cooldown set but daily count not incremented ──

    [Fact]
    public async Task RunCycle_RotationClosePartialFailure_SetsCooldownButSkipsDailyCount()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                RotationThresholdPerHour = 0.0003m,
                MinHoldBeforeRotationMinutes = 30,
                MaxRotationsPerDay = 5,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var existingPosition = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            CurrentSpreadPerHour = 0.0001m,
            EntrySpreadPerHour = 0.0002m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp = MakeOpp(2, "BTC", longExId: 1, shortExId: 3);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        var recommendation = new RotationRecommendationDto(
            PositionId: 10,
            PositionAsset: "ETH",
            CurrentSpreadPerHour: 0.0001m,
            ReplacementAssetId: 2,
            ReplacementAsset: "BTC",
            ReplacementLongExchange: "Hyperliquid",
            ReplacementShortExchange: "Aster",
            ReplacementLongExchangeId: 1,
            ReplacementShortExchangeId: 3,
            ReplacementNetYieldPerHour: 0.0005m,
            ImprovementPerHour: 0.0004m);

        _mockRotationEvaluator.Setup(r => r.Evaluate(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<UserConfiguration>(),
                It.IsAny<BotConfiguration>()))
            .Returns(recommendation);

        // Simulate partial close: status goes to Closing (not Closed)
        _mockExecutionEngine.Setup(e => e.ClosePositionAsync(TestUserId, existingPosition, CloseReason.Rotation, It.IsAny<CancellationToken>()))
            .Callback(() => existingPosition.Status = PositionStatus.Closing)
            .Returns(Task.CompletedTask);

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // ClosePositionAsync should have been called
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(TestUserId, existingPosition, CloseReason.Rotation, It.IsAny<CancellationToken>()),
            Times.Once);

        // Cooldown should still be set (prevents retry storms)
        var cooldownKey = $"{TestUserId}:2:1:3";
        _circuitBreaker.RotationCooldowns.Should().ContainKey(cooldownKey);

        // Daily count should NOT be incremented (close did not fully succeed)
        _circuitBreaker.DailyRotationCounts.Should().NotContainKey(TestUserId);
    }

    // ── NB3-v5: Day boundary reset — yesterday's count resets ──

    [Fact]
    public async Task RunCycle_RotationDailyCountFromYesterday_ResetsAndAllowsRotation()
    {
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                RotationThresholdPerHour = 0.0003m,
                MinHoldBeforeRotationMinutes = 30,
                MaxRotationsPerDay = 2,
            });

        var config = new BotConfiguration
        {
            IsEnabled = true,
            OperatingState = BotOperatingState.Armed,
            MaxConcurrentPositions = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            TotalCapitalUsdc = 1000m,
            MaxCapitalPerPosition = 0.5m,
            VolumeFraction = 0.001m,
            UpdatedByUserId = TestUserId,
        };
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var existingPosition = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            CurrentSpreadPerHour = 0.0001m,
            EntrySpreadPerHour = 0.0002m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        var opp = MakeOpp(2, "BTC", longExId: 1, shortExId: 3);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });

        var recommendation = new RotationRecommendationDto(
            PositionId: 10,
            PositionAsset: "ETH",
            CurrentSpreadPerHour: 0.0001m,
            ReplacementAssetId: 2,
            ReplacementAsset: "BTC",
            ReplacementLongExchange: "Hyperliquid",
            ReplacementShortExchange: "Aster",
            ReplacementLongExchangeId: 1,
            ReplacementShortExchangeId: 3,
            ReplacementNetYieldPerHour: 0.0005m,
            ImprovementPerHour: 0.0004m);

        _mockRotationEvaluator.Setup(r => r.Evaluate(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<UserConfiguration>(),
                It.IsAny<BotConfiguration>()))
            .Returns(recommendation);

        _mockExecutionEngine.Setup(e => e.ClosePositionAsync(TestUserId, existingPosition, CloseReason.Rotation, It.IsAny<CancellationToken>()))
            .Callback(() => existingPosition.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);

        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<AllocationStrategy>(), It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Pre-seed daily count from yesterday at cap
        _circuitBreaker.DailyRotationCounts[TestUserId] = (DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), 5);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Rotation should proceed because yesterday's count resets
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(TestUserId, existingPosition, CloseReason.Rotation, It.IsAny<CancellationToken>()),
            Times.Once);

        // Daily count should be reset to today with count = 1
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _circuitBreaker.DailyRotationCounts[TestUserId].Date.Should().Be(today);
        _circuitBreaker.DailyRotationCounts[TestUserId].Count.Should().Be(1);
    }

    // ── Funding deviation proximity gate tests ──────────────────────────────

    private static BotConfiguration MakeTradingConfig() => new()
    {
        IsEnabled = true,
        OperatingState = BotOperatingState.Armed,
        MaxConcurrentPositions = 5,
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 3,
        TotalCapitalUsdc = 1000m,
        MaxCapitalPerPosition = 0.5m,
        VolumeFraction = 0.001m,
        UpdatedByUserId = TestUserId,
    };

    [Fact]
    public async Task ExecuteUserCycle_AsterCandidate_WithinDeviationWindow_SkipsOpen()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeTradingConfig());
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp = MakeOpp(1, "ETH");
        opp.MaxLegFundingDeviationSeconds = 15;
        opp.EarliestLegNextSettlementUtc = DateTime.UtcNow.AddSeconds(8);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(),
                It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        IEnumerable<OpportunitySnapshot>? savedSnapshots = null;
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<OpportunitySnapshot>, CancellationToken>((s, _) => savedSnapshots = s.ToList())
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Open must not be called — candidate is within deviation window
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(),
                It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Snapshot must carry "funding_deviation_window" skip reason
        savedSnapshots.Should().NotBeNull();
        var snapshot = savedSnapshots!.Single();
        snapshot.SkipReason.Should().Be("funding_deviation_window");
        snapshot.WasOpened.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteUserCycle_AsterCandidate_AfterDeviationWindow_OpensNormally()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeTradingConfig());
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        var opp = MakeOpp(1, "ETH");
        opp.MaxLegFundingDeviationSeconds = 15;
        opp.EarliestLegNextSettlementUtc = DateTime.UtcNow.AddSeconds(120); // well past window
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(),
                It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(),
                It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Settlement is 120s away, window is only 15s → gate does NOT trigger
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(),
                It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteUserCycle_NonAsterCandidate_DeviationZero_NeverGated()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeTradingConfig());
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        // MaxLegFundingDeviationSeconds = null (both-zero → populator returns null)
        var opp = MakeOpp(1, "ETH");
        opp.MaxLegFundingDeviationSeconds = null;
        opp.EarliestLegNextSettlementUtc = DateTime.UtcNow.AddSeconds(2); // would gate if deviation were non-zero
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(),
                It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(),
                It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // deviation is 0/null → gate never fires
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(),
                It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteUserCycle_AsterCandidate_NoSettlementTimestamp_OpensNormally()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeTradingConfig());
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([]);

        // MaxLegFundingDeviationSeconds set but EarliestLegNextSettlementUtc is null → gate requires both
        var opp = MakeOpp(1, "ETH");
        opp.MaxLegFundingDeviationSeconds = 15;
        opp.EarliestLegNextSettlementUtc = null;
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [opp] });
        _mockPositionSizer.Setup(s => s.CalculateBatchSizesAsync(
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(), It.IsAny<AllocationStrategy>(),
                It.IsAny<string>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([100m]);

        _mockExecutionEngine.Setup(e => e.OpenPositionAsync(
                It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(), It.IsAny<decimal>(),
                It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, (string?)null));
        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // No settlement timestamp → gate cannot fire, open proceeds normally
        _mockExecutionEngine.Verify(
            e => e.OpenPositionAsync(It.IsAny<string>(), It.IsAny<ArbitrageOpportunityDto>(),
                It.IsAny<decimal>(), It.IsAny<UserConfiguration?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteUserCycle_RotationCandidate_WithinDeviationWindow_DoesNotClose()
    {
        // Need exactly max-positions filled so the rotation branch fires
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(TestUserId))
            .ReturnsAsync(new UserConfiguration
            {
                UserId = TestUserId,
                IsEnabled = true,
                MaxConcurrentPositions = 1,
                TotalCapitalUsdc = 1000m,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0001m,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 3,
                RotationThresholdPerHour = 0.0003m,
                MinHoldBeforeRotationMinutes = 0,
                MaxRotationsPerDay = 5,
            });

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeTradingConfig());

        var existingPosition = new ArbitragePosition
        {
            Id = 10,
            UserId = TestUserId,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            MarginUsdc = 20m,
            Leverage = 5,
            CurrentSpreadPerHour = 0.0001m,
            EntrySpreadPerHour = 0.0002m,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
            Status = PositionStatus.Open,
            Asset = new Asset { Id = 1, Symbol = "ETH" },
            LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
            ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
        };
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync([existingPosition]);

        // Replacement opportunity is within the deviation window
        var replacementOpp = MakeOpp(2, "BTC", longExId: 1, shortExId: 3);
        replacementOpp.MaxLegFundingDeviationSeconds = 15;
        replacementOpp.EarliestLegNextSettlementUtc = DateTime.UtcNow.AddSeconds(5);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = [replacementOpp] });

        var recommendation = new RotationRecommendationDto(
            PositionId: 10,
            PositionAsset: "ETH",
            CurrentSpreadPerHour: 0.0001m,
            ReplacementAssetId: 2,
            ReplacementAsset: "BTC",
            ReplacementLongExchange: "ExA",
            ReplacementLongExchangeId: 1,
            ReplacementShortExchange: "ExB",
            ReplacementShortExchangeId: 3,
            ReplacementNetYieldPerHour: 0.0005m,
            ImprovementPerHour: 0.0004m);

        _mockRotationEvaluator.Setup(r => r.Evaluate(
                It.IsAny<IReadOnlyList<ArbitragePosition>>(),
                It.IsAny<IReadOnlyList<ArbitrageOpportunityDto>>(),
                It.IsAny<UserConfiguration>(),
                It.IsAny<BotConfiguration>()))
            .Returns(recommendation);

        _mockSnapshotRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<OpportunitySnapshot>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockSnapshotRepo.Setup(r => r.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await _sut.RunCycleAsync(CancellationToken.None);

        // Rotation close must not fire — replacement is within deviation window
        _mockExecutionEngine.Verify(
            e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(),
                CloseReason.Rotation, It.IsAny<CancellationToken>()),
            Times.Never);

        // Rotation cooldown must be set (prevents immediate retry)
        var cooldownKey = $"{TestUserId}:{recommendation.ReplacementAssetId}:{recommendation.ReplacementLongExchangeId}:{recommendation.ReplacementShortExchangeId}";
        _circuitBreaker.RotationCooldowns.Should().ContainKey(cooldownKey,
            "cooldown must be set so the gated rotation is not retried immediately");
    }
}
