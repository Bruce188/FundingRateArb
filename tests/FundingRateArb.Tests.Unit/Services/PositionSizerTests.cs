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
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator = new();
    private readonly PositionSizer _sut;

    private readonly Mock<IPositionRepository> _mockPositions = new();

    public PositionSizerTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockPositions.Setup(p => p.GetOpenAsync()).ReturnsAsync(new List<ArbitragePosition>());
        // Default balance: high enough that config cap applies
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 10_000m, FetchedAt = DateTime.UtcNow });
        // Use real YieldCalculator — no external dependencies
        _sut = new PositionSizer(_mockUow.Object, new YieldCalculator(), _mockBalanceAggregator.Object);
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
    // CalculateOptimalSizeAsync removed (dead code)
    // -----------------------------------------------------------------------

    [Fact]
    public void CalculateOptimalSizeAsync_RemovedFromInterface()
    {
        var methods = typeof(IPositionSizer).GetMethods();
        methods.Should().NotContain(m => m.Name == "CalculateOptimalSizeAsync",
            "CalculateOptimalSizeAsync is dead code and should be removed");
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

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

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

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.WeightedSpread, "test-user");

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

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.EqualSpread, "test-user");

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

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.RiskAdjusted, "test-user");

        // Low volume opp should get less capital
        sizes[0].Should().BeLessThan(sizes[1]);
    }

    // -----------------------------------------------------------------------
    // C2: Notional liquidity check (margin * leverage vs volume limit)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CalculateBatchSizesAsync_CapsMarginByNotionalLiquidity()
    {
        // margin $100, leverage 5x → notional $500
        // volume limit = 400 * 0.001 = 0.4 → notional 500 > 0.4 → cap margin to 0.4/5 = 0.08
        // But with real numbers: volume = 2000, volumeFraction = 0.001 → liquidityLimit = 2
        // notional = margin * 5. If margin = 85.6, notional = 428 > 2 → capped to 2/5 = 0.4
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m, leverage: 5, volumeFraction: 0.001m);
        config.MinPositionSizeUsdc = 0m; // don't filter by min size for this test
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Volume = 400 → liquidityLimit = 400 * 0.001 = 0.4
        // margin = 100, notional = 500 > 0.4 → capped to 0.4 / 5 = 0.08
        var opps = new List<ArbitrageOpportunityDto>
        {
            new()
            {
                AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SpreadPerHour = 0.001m, NetYieldPerHour = 0.001m,
                LongVolume24h = 400m, ShortVolume24h = 400m,
                LongMarkPrice = 100m, ShortMarkPrice = 100m,
            }
        };

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // liquidityLimit = 400 * 0.001 = 0.4, margin = 0.4 / 5 = 0.08
        sizes[0].Should().Be(0.08m);
    }

    // -----------------------------------------------------------------------
    // C1: Breakeven rejection in batch path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CalculateBatchSizesAsync_ZeroesOutPositionsThatCantBreakEven()
    {
        // SpreadPerHour = 0.002, NetYieldPerHour = 0.0001
        // entryFeeRate = 0.002 - 0.0001 = 0.0019
        // breakEvenHours = 0.0019 / 0.0001 = 19 hours
        // BreakevenHoursMax = 6 → rejected
        var config = DefaultConfig();
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var opps = new List<ArbitrageOpportunityDto>
        {
            new()
            {
                AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SpreadPerHour = 0.002m, NetYieldPerHour = 0.0001m,
                LongVolume24h = 100_000_000m, ShortVolume24h = 100_000_000m,
                LongMarkPrice = 100m, ShortMarkPrice = 100m,
            }
        };

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(0m, "breakeven hours (19) exceeds BreakevenHoursMax (6)");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_KeepsPositionsThatBreakEvenInTime()
    {
        // SpreadPerHour = 0.001, NetYieldPerHour = 0.001 (zero fee scenario)
        // entryFeeRate = 0, breakEvenHours = 0 → passes
        var config = DefaultConfig();
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var opps = MakeOpps((0.001m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_RejectsNegativeEntryFeeRate()
    {
        // Corrupted: NetYieldPerHour > SpreadPerHour → negative entryFeeRate
        var config = DefaultConfig();
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var opps = new List<ArbitrageOpportunityDto>
        {
            new()
            {
                AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SpreadPerHour = 0.0005m, NetYieldPerHour = 0.0008m,
                LongVolume24h = 100_000_000m, ShortVolume24h = 100_000_000m,
                LongMarkPrice = 100m, ShortMarkPrice = 100m,
            }
        };

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(0m, "negative entryFeeRate means corrupted opportunity");
    }

    // -----------------------------------------------------------------------
    // H2: Minimum position size enforcement
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CalculateBatchSizesAsync_ZeroesOutPositionsBelowMinSize()
    {
        // totalCapital = 10 * 0.80 = 8, MinPositionSizeUsdc = 10 → 8 < 10 → zeroed
        var config = DefaultConfig(totalCapital: 10m, maxCapitalPerPos: 0.80m);
        config.MinPositionSizeUsdc = 10m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var opps = MakeOpps((0.001m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(0m, "position size 8 < MinPositionSizeUsdc 10");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_KeepsPositionsAboveMinSize()
    {
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 0.80m);
        config.MinPositionSizeUsdc = 10m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var opps = MakeOpps((0.001m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(80m, "position size 80 >= MinPositionSizeUsdc 10");
    }

    // -----------------------------------------------------------------------
    // D6: Capital subtraction and empty opportunities
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CalculateBatchSizesAsync_SubtractsOpenPositionCapital()
    {
        // Config: TotalCapitalUsdc=1000, MaxCapitalPerPosition=0.8
        // Open positions consuming 500 USDC
        // Available = Math.Max(0, 1000 - 500) = 500
        // totalCapital = 500 * 0.8 = 400
        var config = DefaultConfig(totalCapital: 1000m, maxCapitalPerPos: 0.80m);
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        _mockPositions.Setup(p => p.GetOpenAsync())
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { SizeUsdc = 300m },
                new() { SizeUsdc = 200m },
            });

        var opps = MakeOpps((0.001m, 100_000_000m));

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(400m, "availableCapital = (1000-500)*0.8 = 400");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_EmptyOpportunities_ReturnsEmpty()
    {
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig());

        var sizes = await _sut.CalculateBatchSizesAsync(
            new List<ArbitrageOpportunityDto>(), AllocationStrategy.Concentrated, "test-user");

        sizes.Should().BeEmpty();
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_RealBalanceBelowConfigCap_UsesRealBalance()
    {
        // Real balance = 80, config cap = 107 → uses 80
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync("test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 80m, FetchedAt = DateTime.UtcNow });
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig());

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available = min(80, 107) * 0.80 = 64
        sizes[0].Should().Be(64m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_RealBalanceAboveConfigCap_UsesConfigCap()
    {
        // Real balance = 200, config cap = 107 → uses 107
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync("test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto { TotalAvailableUsdc = 200m, FetchedAt = DateTime.UtcNow });
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig());

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available = min(200, 107) * 0.80 = 85.6
        sizes[0].Should().Be(85.6m);
    }
}
