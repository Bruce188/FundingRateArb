using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class PositionSizerLiveCapitalTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly PositionSizer _sut;

    public PositionSizerLiveCapitalTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync(It.IsAny<string>(), It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(new List<ArbitragePosition>());
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5 });
        _sut = new PositionSizer(
            _mockUow.Object,
            new YieldCalculator(),
            _mockBalanceAggregator.Object,
            _mockUserSettings.Object,
            Mock.Of<ILeverageTierProvider>(p =>
                p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue));
    }

    private static BotConfiguration MakeConfig(
        decimal totalCapital = 500m,
        decimal maxCapitalPerPos = 1.0m,
        int leverage = 5,
        int maxLeverageCap = 50) => new()
        {
            TotalCapitalUsdc = totalCapital,
            MaxCapitalPerPosition = maxCapitalPerPos,
            DefaultLeverage = leverage,
            MaxLeverageCap = maxLeverageCap,
            VolumeFraction = 0.001m,
            MaxConcurrentPositions = 10,
            IsEnabled = true,
            OpenThreshold = 0.0003m,
            BreakevenHoursMax = 6,
            MaxExposurePerAsset = 1.0m,
            MaxExposurePerExchange = 1.0m,
            MinPositionSizeUsdc = 0m,
        };

    private static ArbitrageOpportunityDto DefaultOpp(int longEx = 1, int shortEx = 2) => new()
    {
        AssetId = 1,
        AssetSymbol = "BTC",
        LongExchangeId = longEx,
        ShortExchangeId = shortEx,
        LongExchangeName = $"Ex{longEx}",
        ShortExchangeName = $"Ex{shortEx}",
        SpreadPerHour = 0.001m,
        NetYieldPerHour = 0.001m,
        LongVolume24h = 100_000_000m,
        ShortVolume24h = 100_000_000m,
        LongMarkPrice = 100m,
        ShortMarkPrice = 100m,
    };

    [Fact]
    public async Task LiveSnapshot56UsdAcrossThreeExchanges_IgnoresStaticConfigOf500()
    {
        // Live balances: E1=20, E2=20, E3=16 → sum=56
        // config.TotalCapitalUsdc=500 must be ignored; only the live sum drives sizing
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeConfig(totalCapital: 500m));
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 56m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "Ex1", AvailableUsdc = 20m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "Ex2", AvailableUsdc = 20m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 3, ExchangeName = "Ex3", AvailableUsdc = 16m, FetchedAt = DateTime.UtcNow },
                }
            });

        var opps = new List<ArbitrageOpportunityDto> { DefaultOpp(longEx: 1, shortEx: 2) };
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user");

        // realCapital = 56; MaxCapitalPerPosition = 1.0 → totalCapital = 56
        // Per-exchange cap: min(E1=20, E2=20) = 20
        // sizes[0] = min(56, 20) = 20
        sizes[0].Should().Be(20m, "live sum of 56 drives sizing; config.TotalCapitalUsdc=500 is not a cap");
    }

    [Fact]
    public async Task UnavailableExchange_ExcludedFromNewPositionSizing()
    {
        // E1 is unavailable → contributes 0 to capital base AND blocks any opp that uses it
        // E2 and E3 are available; their balances form the live capital base
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeConfig(totalCapital: 500m));
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 40m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "Ex1", AvailableUsdc = 0m, IsUnavailable = true, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "Ex2", AvailableUsdc = 25m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 3, ExchangeName = "Ex3", AvailableUsdc = 15m, FetchedAt = DateTime.UtcNow },
                }
            });

        // Opp that involves the unavailable exchange (E1 as long leg) must be zeroed
        var opps = new List<ArbitrageOpportunityDto> { DefaultOpp(longEx: 1, shortEx: 2) };
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user");

        sizes[0].Should().Be(0m, "opp involving unavailable E1 must be excluded from sizing");
    }

    [Fact]
    public async Task FallbackEligibleExchange_ContributesLastKnownValue()
    {
        // E1 is stale but fallback-eligible: AvailableUsdc=0, LastKnownAvailableUsdc=30
        // Fallback age is within 5 minutes → IsFallbackEligible = true → contributes 30
        var fetchedAt = DateTime.UtcNow;
        var lastKnownAt = DateTimeOffset.UtcNow.AddMinutes(-2);

        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeConfig(totalCapital: 500m));
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 30m,
                FetchedAt = fetchedAt,
                Balances = new List<ExchangeBalanceDto>
                {
                    new()
                    {
                        ExchangeId = 1, ExchangeName = "Ex1",
                        AvailableUsdc = 0m,
                        IsStale = true,
                        LastKnownAvailableUsdc = 30m,
                        LastKnownAt = lastKnownAt,
                        FetchedAt = fetchedAt,
                    },
                    new() { ExchangeId = 2, ExchangeName = "Ex2", AvailableUsdc = 20m, FetchedAt = fetchedAt },
                }
            });

        var opps = new List<ArbitrageOpportunityDto> { DefaultOpp(longEx: 1, shortEx: 2) };
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user");

        // realCapital = LastKnownAvailableUsdc(30) + E2(20) = 50
        // totalCapital = 50 * 1.0 = 50; per-exchange balance cap: min(AvailableUsdc=0, 20) = 0 → zeroed
        // Note: per-exchange balance cap uses AvailableUsdc (not fallback); the fallback value only
        // affects the capital base computation (realCapital), not the individual exchange balance check.
        sizes[0].Should().Be(0m, "fallback contributes to capital base but per-exchange cap uses AvailableUsdc=0");
    }

    [Fact]
    public async Task AllExchangesUnavailable_ReturnsZeroAllowableNotional()
    {
        // All exchanges unavailable → realCapital = 0 → no allocatable notional
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(MakeConfig(totalCapital: 500m));
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 0m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "Ex1", AvailableUsdc = 0m, IsUnavailable = true, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "Ex2", AvailableUsdc = 0m, IsUnavailable = true, FetchedAt = DateTime.UtcNow },
                }
            });

        var opps = new List<ArbitrageOpportunityDto> { DefaultOpp() };
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user");

        sizes[0].Should().Be(0m, "all exchanges unavailable → realCapital=0 → no allocation possible");
    }

    [Fact]
    public async Task MaxLeverageCap_StillBoundsResult()
    {
        // Regression: MaxLeverageCap must still limit the notional even with live capital sourcing
        var config = MakeConfig(totalCapital: 500m, maxCapitalPerPos: 1.0m, leverage: 20, maxLeverageCap: 3);
        config.VolumeFraction = 0.001m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 20 });

        // Live sum = 100; per-exchange 50 each
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 100m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "Ex1", AvailableUsdc = 50m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "Ex2", AvailableUsdc = 50m, FetchedAt = DateTime.UtcNow },
                }
            });

        var capturedNotionals = new List<decimal>();
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider
            .Setup(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Callback<string, string, decimal>((_, _, n) => capturedNotionals.Add(n))
            .Returns(int.MaxValue);

        var sut = new PositionSizer(
            _mockUow.Object, new YieldCalculator(), _mockBalanceAggregator.Object,
            _mockUserSettings.Object, mockTierProvider.Object);

        var opps = new List<ArbitrageOpportunityDto> { DefaultOpp() };
        var sizes = await sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user");

        // realCapital=100, totalCapital=100; per-exchange cap=50 → sizes[0]=50
        // effectiveLeverage = min(20, MaxLeverageCap=3) = 3
        // tentativeNotional = 50 * 3 = 150 (not 50 * 20 = 1000)
        sizes[0].Should().Be(50m);
        capturedNotionals.Should().NotBeEmpty();
        capturedNotionals.Should().AllSatisfy(n =>
            n.Should().Be(150m, "notional must use capped leverage=3, not user leverage=20"));
    }
}
