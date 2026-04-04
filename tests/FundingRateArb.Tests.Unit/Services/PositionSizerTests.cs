using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class PositionSizerTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly PositionSizer _sut;

    private readonly Mock<IPositionRepository> _mockPositions = new();

    public PositionSizerTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync(It.IsAny<string>(), It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>());
        // Default balance: high enough that config cap applies
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 10_000m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "Exchange1", AvailableUsdc = 5_000m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "Exchange2", AvailableUsdc = 5_000m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 3, ExchangeName = "Exchange3", AvailableUsdc = 5_000m, FetchedAt = DateTime.UtcNow },
                }
            });
        // Default: user leverage matches bot config (5x)
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 5 });
        // Use real YieldCalculator — no external dependencies
        _sut = new PositionSizer(_mockUow.Object, new YieldCalculator(), _mockBalanceAggregator.Object, _mockUserSettings.Object, Mock.Of<ILeverageTierProvider>(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()) == int.MaxValue));
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
        int maxConcurrentPositions = 1,
        int maxLeverageCap = 50) => new()
        {
            TotalCapitalUsdc = totalCapital,
            MaxCapitalPerPosition = maxCapitalPerPos,
            DefaultLeverage = leverage,
            MaxLeverageCap = maxLeverageCap,
            VolumeFraction = volumeFraction,
            MaxConcurrentPositions = maxConcurrentPositions,
            IsEnabled = true,
            OpenThreshold = 0.0003m,
            BreakevenHoursMax = 6,
            MaxExposurePerAsset = 1.0m,
            MaxExposurePerExchange = 1.0m,
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
    [InlineData(1.239, 0.01, 2, 1.23)]
    [InlineData(100.0, 0.1, 1, 100.0)]
    [InlineData(0.0056, 0.001, 3, 0.005)]
    [InlineData(10.0, 0.25, 2, 10.0)]
    [InlineData(10.1, 0.25, 2, 10.0)]
    [InlineData(0.0, 0.01, 2, 0.0)]
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

    [Fact]
    public async Task CalculateBatchSizesAsync_LiquidityCapUsesUserLeverage()
    {
        // Bot config leverage = 5, user leverage = 2
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m, leverage: 5, volumeFraction: 0.001m);
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 2 });

        // Volume = 400 -> liquidityLimit = 400 * 0.001 = 0.4
        // With user leverage = 2: notional = margin * 2, capped to 0.4 / 2 = 0.2
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

        // liquidityLimit = 0.4, user leverage = 2 → margin = 0.4 / 2 = 0.2
        sizes[0].Should().Be(0.2m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_DefensiveFloor_ClampsLeverageTo1_WhenBothConfigsAreZero()
    {
        // Both user and bot config have leverage=0 (corrupted DB) — floor to 1
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m, leverage: 0, volumeFraction: 0.001m);
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 0 });

        // Volume = 400 -> liquidityLimit = 400 * 0.001 = 0.4
        // With floor leverage = 1: notional = margin * 1, capped to 0.4 / 1 = 0.4
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

        // liquidityLimit = 0.4, floor leverage = 1 → margin = 0.4 / 1 = 0.4
        sizes[0].Should().Be(0.4m);
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

    [Fact]
    public async Task CalculateBatchSizesAsync_BreakevenUsesRawNetNotBoosted()
    {
        // SpreadPerHour = 0.002, NetYieldPerHour (raw) = 0.0001
        // entryFeeRate = 0.002 - 0.0001 = 0.0019
        // breakEvenHours = 0.0019 / 0.0001 = 19 hours → exceeds BreakevenHoursMax (6) → rejected
        // BoostedNetYieldPerHour = 0.005 would give entryFeeRate = -0.003 → would be rejected differently
        // This proves break-even uses raw NetYieldPerHour, not BoostedNetYieldPerHour
        var config = DefaultConfig();
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        var opps = new List<ArbitrageOpportunityDto>
        {
            new()
            {
                AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SpreadPerHour = 0.002m,
                NetYieldPerHour = 0.0001m,
                BoostedNetYieldPerHour = 0.005m,
                LongVolume24h = 100_000_000m, ShortVolume24h = 100_000_000m,
                LongMarkPrice = 100m, ShortMarkPrice = 100m,
            }
        };

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(0m, "break-even uses raw NetYieldPerHour (19hrs > 6hr max), not BoostedNetYieldPerHour");
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

        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { SizeUsdc = 300m, UserId = "test-user" },
                new() { SizeUsdc = 200m, UserId = "test-user" },
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
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 80m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "E1", AvailableUsdc = 80m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "E2", AvailableUsdc = 80m, FetchedAt = DateTime.UtcNow },
                }
            });
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
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 200m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "E1", AvailableUsdc = 200m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "E2", AvailableUsdc = 200m, FetchedAt = DateTime.UtcNow },
                }
            });
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(DefaultConfig());

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available = min(200, 107) * 0.80 = 85.6
        sizes[0].Should().Be(85.6m);
    }

    // ── Exposure limit tests ────────────────────────────────────────────────

    [Fact]
    public async Task CalculateBatchSizesAsync_CappedByAssetExposureLimit()
    {
        // Config: MaxExposurePerAsset = 0.3 (30% of 100 = 30 USDC max per asset)
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m);
        config.MaxExposurePerAsset = 0.3m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Existing open position for asset 1 with 20 USDC
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>
        {
            new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SizeUsdc = 20m, UserId = "test-user" }
        });

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available = (100 - 20) * 1.0 = 80 from capital, but asset limit = 30 - 20 = 10
        sizes[0].Should().Be(10m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_CappedByExchangeExposureLimit()
    {
        // Config: MaxExposurePerExchange = 0.4 (40% of 100 = 40 USDC max per exchange)
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m);
        config.MaxExposurePerAsset = 1.0m;
        config.MaxExposurePerExchange = 0.4m;
        config.MinPositionSizeUsdc = 1m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Existing open position on exchange 1 with 35 USDC
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>
        {
            new() { AssetId = 2, LongExchangeId = 1, ShortExchangeId = 3, SizeUsdc = 35m, UserId = "test-user" }
        });

        // New opportunity uses exchange 1 as long
        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Capital available = (100 - 35) * 1.0 = 65, but exchange limit = 40 - 35 = 5
        sizes[0].Should().Be(5m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_WithinLimits_NoReduction()
    {
        // Config: generous limits, should not cap
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 0.8m);
        config.MaxExposurePerAsset = 1.0m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>());

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Full capital allocation: 100 * 0.8 = 80
        sizes[0].Should().Be(80m);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_TwoOpportunitiesSameAsset_CombinedRespectAssetLimit()
    {
        // Config: MaxExposurePerAsset = 0.3 (30% of 100 = 30 USDC max per asset)
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m);
        config.MaxExposurePerAsset = 0.3m;
        config.MaxExposurePerExchange = 1.0m;
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>());

        // Two opportunities for the same asset on different exchanges
        var opps = new List<ArbitrageOpportunityDto>
        {
            new()
            {
                AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2,
                SpreadPerHour = 0.001m, NetYieldPerHour = 0.001m,
                LongVolume24h = 100_000_000m, ShortVolume24h = 100_000_000m,
                LongMarkPrice = 100m, ShortMarkPrice = 100m,
            },
            new()
            {
                AssetId = 1, LongExchangeId = 3, ShortExchangeId = 4,
                SpreadPerHour = 0.001m, NetYieldPerHour = 0.001m,
                LongVolume24h = 100_000_000m, ShortVolume24h = 100_000_000m,
                LongMarkPrice = 100m, ShortMarkPrice = 100m,
            },
        };

        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.EqualSpread, "test-user");

        // Combined allocation for asset 1 must not exceed 30 USDC
        (sizes[0] + sizes[1]).Should().BeLessOrEqualTo(30m,
            "combined batch allocation for the same asset must respect MaxExposurePerAsset");
        // Both should get some allocation (equal spread: 50 each, but capped by asset limit)
        sizes[0].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_LowBalance_ExposureLimitUsesRealCapital()
    {
        // Config: TotalCapitalUsdc=107, MaxExposurePerAsset=0.5
        // Balance: 50 USDC → realCapital = min(50, 107) = 50
        // Exposure cap = 0.5 * 50 = 25 USDC (not 0.5 * 107 = 53.5)
        var config = DefaultConfig(totalCapital: 107m, maxCapitalPerPos: 1.0m);
        config.MaxExposurePerAsset = 0.5m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockBalanceAggregator.Setup(b => b.GetBalanceSnapshotAsync("test-user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BalanceSnapshotDto
            {
                TotalAvailableUsdc = 50m,
                FetchedAt = DateTime.UtcNow,
                Balances = new List<ExchangeBalanceDto>
                {
                    new() { ExchangeId = 1, ExchangeName = "Exchange1", AvailableUsdc = 25m, FetchedAt = DateTime.UtcNow },
                    new() { ExchangeId = 2, ExchangeName = "Exchange2", AvailableUsdc = 25m, FetchedAt = DateTime.UtcNow },
                }
            });
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>());

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available capital = 50 * 1.0 = 50, but asset exposure cap = 0.5 * 50 = 25
        sizes[0].Should().Be(25m, "exposure limit should use realCapital, not config.TotalCapitalUsdc");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_ExposureAlreadyAtLimit_ReturnsZero()
    {
        // Config: MaxExposurePerAsset = 0.5 (50 USDC for 100 total)
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m);
        config.MaxExposurePerAsset = 0.5m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // Existing positions already at the 50 USDC asset limit
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>())).ReturnsAsync(new List<ArbitragePosition>
        {
            new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SizeUsdc = 50m, UserId = "test-user" }
        });

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        sizes[0].Should().Be(0m, "asset exposure limit already reached");
    }

    // ── Opening positions counted in capital allocation ────────────────────

    [Fact]
    public async Task CalculateBatchSizesAsync_OpeningPositions_CountedInAllocatedCapital()
    {
        // 1 Open (50 USDC) + 1 Opening (50 USDC) = 100 allocated
        // TotalCapital = 1000, available = 1000 - 100 = 900, * 0.8 = 720
        var config = DefaultConfig(totalCapital: 1000m, maxCapitalPerPos: 0.80m);
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { SizeUsdc = 50m, Status = PositionStatus.Open, UserId = "test-user" },
                new() { SizeUsdc = 50m, Status = PositionStatus.Opening, UserId = "test-user" },
            });

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available = (1000 - 100) * 0.80 = 720
        sizes[0].Should().Be(720m, "Opening positions should reduce available capital");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_OpeningPositions_ReduceAvailableSlots()
    {
        // 1 Open + 1 Opening on the same asset: total asset exposure = 400
        // MaxExposurePerAsset = 0.5 → max = 0.5 * 1000 = 500, remaining = 500 - 400 = 100
        var config = DefaultConfig(totalCapital: 1000m, maxCapitalPerPos: 0.80m);
        config.MinPositionSizeUsdc = 0m;
        config.MaxExposurePerAsset = 0.5m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SizeUsdc = 200m, Status = PositionStatus.Open, UserId = "test-user" },
                new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SizeUsdc = 200m, Status = PositionStatus.Opening, UserId = "test-user" },
            });

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // Available capital = (1000 - 400) * 0.8 = 480
        // Asset exposure cap: 0.5 * 1000 = 500, current = 400, remaining = 100
        // sizes[0] = min(480, 100) = 100
        sizes[0].Should().Be(100m, "Opening positions should be included in asset exposure calculations");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_SingleQueryReturnsOpenAndOpening()
    {
        // Verifies GetByUserAndStatusesAsync is called with both Open and Opening statuses
        var config = DefaultConfig(totalCapital: 1000m, maxCapitalPerPos: 0.80m);
        config.MinPositionSizeUsdc = 0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("test-user", PositionStatus.Open, PositionStatus.Opening))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { Id = 1, SizeUsdc = 50m, Status = PositionStatus.Open, UserId = "test-user" },
                new() { Id = 2, SizeUsdc = 50m, Status = PositionStatus.Opening, UserId = "test-user" },
            });

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "test-user");

        // allocatedCapital = 100, available = (1000-100)*0.8 = 720
        sizes[0].Should().Be(720m, "single query should return both Open and Opening positions");
        _mockPositions.Verify(p => p.GetByUserAndStatusesAsync("test-user", PositionStatus.Open, PositionStatus.Opening), Times.Once);
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_IgnoresOtherUserPositions()
    {
        // User A has 300 USDC allocated, User B has 500 USDC allocated
        // When sizing for User A, only User A's 300 should be subtracted
        var config = DefaultConfig(totalCapital: 1000m, maxCapitalPerPos: 1.0m);
        config.MinPositionSizeUsdc = 0m;
        config.MaxExposurePerAsset = 1.0m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // GetByUserAndStatusesAsync pushes filter into SQL — only user-a's positions returned
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("user-a", It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new() { UserId = "user-a", AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SizeUsdc = 300m },
            });

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user-a");

        // Available = min(10000, 1000) - 300 (only user-a) = 700, * 1.0 = 700
        sizes[0].Should().Be(700m, "only user-a's positions (300) should be subtracted, not user-b's (500)");
    }

    [Fact]
    public async Task CalculateBatchSizesAsync_IgnoresOtherUserExposureLimits()
    {
        // User A has 0 positions, User B has 400 USDC on asset 1
        // MaxExposurePerAsset = 0.5 → cap = 0.5 * 1000 = 500
        // User A should get 500 (50% cap), not 100 (500-400)
        var config = DefaultConfig(totalCapital: 1000m, maxCapitalPerPos: 1.0m);
        config.MinPositionSizeUsdc = 0m;
        config.MaxExposurePerAsset = 0.5m;
        config.MaxExposurePerExchange = 1.0m;
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);

        // GetByUserAndStatusesAsync for user-a returns empty — user-a has no positions
        _mockPositions.Setup(p => p.GetByUserAndStatusesAsync("user-a", It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(new List<ArbitragePosition>());

        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await _sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user-a");

        // Available = min(10000, 1000) - 0 = 1000, * 1.0 = 1000
        // Asset exposure cap = 0.5 * 1000 = 500
        // User A should get 500 (not reduced by User B's positions)
        sizes[0].Should().Be(500m, "user-a's asset exposure should not include user-b's 400 USDC position");
    }

    // ── NB5: Tier-constrained leverage reduces notional calculation ──────────

    [Fact]
    public async Task CalculateBatchSizes_TierConstrainsLeverage_ReducesNotionalCalculation()
    {
        // Config leverage = 10x, tier returns 3x
        // Liquidity cap should use 3x notional, not 10x
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m, leverage: 10, volumeFraction: 0.001m);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 10 });

        // Tier provider returns 3x for any lookup
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider.Setup(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Returns(3);

        var sut = new PositionSizer(_mockUow.Object, new YieldCalculator(), _mockBalanceAggregator.Object, _mockUserSettings.Object, mockTierProvider.Object);

        // Volume = 1M, fraction = 0.001 → liquidity cap = 1000
        // With tier cap 3x: notional = size * 3, cap check uses 3x
        var opps = MakeOpps((0.001m, 1_000_000m));
        var sizes = await sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user-1");

        // At 10x: notional = 100 * 10 = 1000, liquidityLimit = 1000, size stays 100
        // At 3x: notional = 100 * 3 = 300, liquidityLimit = 1000, 300 < 1000 → size stays 100
        // The key assertion: the tier provider was consulted with the tentative notional
        mockTierProvider.Verify(
            p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()),
            Times.AtLeastOnce);
    }

    // ── NB8: MaxLeverageCap limits effective leverage ────────────────────────

    [Fact]
    public async Task CalculateBatchSizes_MaxLeverageCap_LimitsEffectiveLeverage()
    {
        // DefaultLeverage=10, MaxLeverageCap=2 → effective leverage should be 2
        var config = DefaultConfig(totalCapital: 100m, maxCapitalPerPos: 1.0m, leverage: 10, volumeFraction: 0.001m, maxLeverageCap: 2);
        _mockBotConfig.Setup(b => b.GetActiveAsync()).ReturnsAsync(config);
        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { DefaultLeverage = 10 });

        // Capture the notional argument passed to GetEffectiveMaxLeverage
        var capturedNotionals = new List<decimal>();
        var mockTierProvider = new Mock<ILeverageTierProvider>();
        mockTierProvider.Setup(p => p.GetEffectiveMaxLeverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()))
            .Callback<string, string, decimal>((_, _, notional) => capturedNotionals.Add(notional))
            .Returns(int.MaxValue);

        var sut = new PositionSizer(_mockUow.Object, new YieldCalculator(), _mockBalanceAggregator.Object, _mockUserSettings.Object, mockTierProvider.Object);

        // Volume = 100M → liquidity limit = 100K (not binding)
        var opps = MakeOpps((0.001m, 100_000_000m));
        var sizes = await sut.CalculateBatchSizesAsync(opps, AllocationStrategy.Concentrated, "user-1");

        // Available capital = min(10000, 100) - 0 = 100, * 1.0 = 100
        // With MaxLeverageCap=2: effectiveLeverage = min(10, 2) = 2
        // tentativeNotional = 100 * 2 = 200, NOT 100 * 10 = 1000
        sizes[0].Should().Be(100m);
        capturedNotionals.Should().NotBeEmpty();
        capturedNotionals.Should().AllSatisfy(n => n.Should().Be(200m, "notional should reflect leverage=2 (capped), not leverage=10"));
    }
}
