using Moq;
using FluentAssertions;
using FundingRateArb.Application.Services;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Services;

public class PositionSizerTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly PositionSizer _sut;

    public PositionSizerTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        // Use real YieldCalculator — no external dependencies
        _sut = new PositionSizer(_mockUow.Object, new YieldCalculator());
    }

    private static ArbitrageOpportunityDto DefaultOpp(
        decimal netYieldPerHour = 0.0005m,
        decimal longVolume24h = 100_000_000m,
        decimal shortVolume24h = 100_000_000m,
        decimal? spreadPerHour = null) => new()
    {
        AssetId = 1,
        LongExchangeId = 1,
        ShortExchangeId = 2,
        LongRatePerHour = 0.0008m,
        ShortRatePerHour = 0.0003m,
        // SpreadPerHour is the gross spread (before fees). Must be >= NetYieldPerHour.
        // Default: same as netYieldPerHour (zero-fee scenario — fees amortized separately).
        SpreadPerHour = spreadPerHour ?? netYieldPerHour,
        NetYieldPerHour = netYieldPerHour,
        LongVolume24h = longVolume24h,
        ShortVolume24h = shortVolume24h,
        LongMarkPrice = 100m,
        ShortMarkPrice = 100m
    };

    private static BotConfiguration DefaultConfig(
        decimal totalCapital = 107m,
        decimal maxCapitalPerPos = 0.80m,
        int leverage = 5,
        decimal volumeFraction = 0.001m,
        int maxConcurrentPositions = 1) => new()
    {
        TotalCapitalUsdc = totalCapital,
        MaxCapitalPerPosition = maxCapitalPerPos,
        DefaultLeverage = leverage,
        VolumeFraction = volumeFraction,
        MaxConcurrentPositions = maxConcurrentPositions,
        IsEnabled = true,
        OpenThreshold = 0.0003m,
        BreakevenHoursMax = 6
    };

    // -----------------------------------------------------------------------
    // CalculateOptimalSizeAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CalculateOptimalSize_ReturnsCapitalLimit_WhenSmallestOfThree()
    {
        // capitalLimit = 107 * 0.80 = 85.6 (collateral per leg; connectors apply leverage)
        // liquidityLimit = min(100_000_000, 100_000_000) * 0.001 = 100_000
        // capital (85.6) < liquidity (100_000)  →  expect 85.6
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opp = DefaultOpp(netYieldPerHour: 0.0005m,
                             longVolume24h: 100_000_000m,
                             shortVolume24h: 100_000_000m);

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(85.6m);
    }

    [Fact]
    public async Task CalculateOptimalSize_ReturnsLiquidityLimit_WhenSmallestOfThree()
    {
        // capitalLimit = 107 * 0.80 = 85.6 (collateral per leg; connectors apply leverage)
        // liquidityLimit = min(10_000, 10_000) * 0.001 = 10
        // liquidity (10) < capital (85.6)  →  expect 10
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opp = DefaultOpp(netYieldPerHour: 0.0005m,
                             longVolume24h: 10_000m,
                             shortVolume24h: 10_000m);

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(10m);
    }

    [Fact]
    public async Task CalculateOptimalSize_ReturnsZero_WhenZeroNetYield()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opp = DefaultOpp(netYieldPerHour: 0m);

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateOptimalSize_ReturnsZero_WhenNegativeNetYield()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opp = DefaultOpp(netYieldPerHour: -0.0001m);

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(0m);
    }

    [Fact]
    public async Task CalculateOptimalSize_ReturnsZero_WhenEntryFeeRateIsNegative()
    {
        // H4: SpreadPerHour < NetYieldPerHour produces negative entryFeeRate
        // This is an invalid/corrupted opportunity and must be rejected
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        // SpreadPerHour defaults to LongRatePerHour - ShortRatePerHour = 0.0008 - 0.0003 = 0.0005
        // But we override NetYieldPerHour to be HIGHER than SpreadPerHour
        // entryFeeRate = SpreadPerHour(0.0005) - NetYieldPerHour(0.0008) = -0.0003 → guard triggers
        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SpreadPerHour = 0.0005m,
            NetYieldPerHour = 0.0008m,   // net yield > gross spread: impossible/corrupted
            LongVolume24h = 100_000_000m,
            ShortVolume24h = 100_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(0m, "negative entryFeeRate signals a corrupted opportunity and must be rejected");
    }

    [Fact]
    public async Task CalculateOptimalSize_DoesNotApplyLeverage_ToCapitalLimit()
    {
        // capitalLimit = 107 * 0.80 = 85.6 (leverage is NOT applied here — connectors handle it)
        // liquidityLimit = min(100_000_000, 100_000_000) * 0.001 = 100_000
        // capital (85.6) < liquidity (100_000)  →  expect 85.6
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig(leverage: 10));

        var opp = DefaultOpp(netYieldPerHour: 0.0005m,
                             longVolume24h: 100_000_000m,
                             shortVolume24h: 100_000_000m);

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(85.6m);
    }

    [Fact]
    public async Task CalculateOptimalSize_UsesMinVolume_ForLiquidityLimit()
    {
        // capitalLimit = 107 * 0.80 = 85.6 (collateral per leg; connectors apply leverage)
        // liquidityLimit = min(10_000, 500_000) * 0.001 = 10
        // liquidity (10) < capital (85.6)  →  expect 10
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opp = DefaultOpp(netYieldPerHour: 0.0005m,
                             longVolume24h: 10_000m,
                             shortVolume24h: 500_000m);

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(10m);
    }

    // -----------------------------------------------------------------------
    // RoundToStepSize (static — no mock needed)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1.23456, 0.01, 2, 1.23)]
    [InlineData(1.239,   0.01, 2, 1.23)]
    [InlineData(100.0,   0.1,  1, 100.0)]
    [InlineData(0.0056,  0.001, 3, 0.005)]
    [InlineData(10.0,    0.25, 2, 10.0)]
    [InlineData(10.1,    0.25, 2, 10.0)]
    [InlineData(0.0,     0.01, 2, 0.0)]
    public void RoundToStepSize_ReturnsCorrectValue(
        decimal quantity, decimal stepSize, int decimals, decimal expected)
    {
        var result = PositionSizer.RoundToStepSize(quantity, stepSize, decimals);
        result.Should().Be(expected);
    }

    [Fact]
    public void RoundToStepSize_FallsBackToRounding_WhenStepSizeIsZero()
    {
        var result = PositionSizer.RoundToStepSize(1.2345m, 0m, 2);
        result.Should().Be(1.23m);
    }

    // -----------------------------------------------------------------------
    // CalculateBatchSizesAsync
    // -----------------------------------------------------------------------

    private static List<ArbitrageOpportunityDto> MakeOpps(params (decimal yield, decimal vol)[] items) =>
        items.Select((x, i) => new ArbitrageOpportunityDto
        {
            AssetId = i + 1,
            AssetSymbol = $"ASSET{i}",
            LongExchangeId = 1,
            ShortExchangeId = 2,
            LongExchangeName = "ExA",
            ShortExchangeName = "ExB",
            SpreadPerHour = x.yield,
            NetYieldPerHour = x.yield,
            LongVolume24h = x.vol,
            ShortVolume24h = x.vol,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        }).ToList();

    [Fact]
    public async Task CalculateBatchSizesAsync_Concentrated_SizesOnlyFirst()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opps = MakeOpps((0.001m, 100_000_000m), (0.0005m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated);

        sizes[0].Should().BeGreaterThan(0);
        sizes[1].Should().Be(0);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_WeightedSpread_DistributesProportionally()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        // First has 3x the yield of second
        var opps = MakeOpps((0.003m, 100_000_000m), (0.001m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.WeightedSpread);

        sizes[0].Should().BeGreaterThan(sizes[1]);
        // First should get ~75%, second ~25%
        sizes[0].Should().BeApproximately(sizes[1] * 3, 0.01m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_EqualSpread_DividesEvenly()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        var opps = MakeOpps((0.001m, 100_000_000m), (0.002m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.EqualSpread);

        sizes[0].Should().Be(sizes[1]);
        // Total = 107 * 0.80 = 85.6, each = 42.8
        sizes[0].Should().Be(42.8m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_RiskAdjusted_PenalizesLowVolume()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(DefaultConfig());

        // Same yield, but first has much lower volume
        var opps = MakeOpps((0.001m, 1_000m), (0.001m, 1_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.RiskAdjusted);

        // Low volume opp should get less capital
        sizes[0].Should().BeLessThan(sizes[1]);
    }
}
