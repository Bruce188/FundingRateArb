using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Moq;

namespace FundingRateArb.Tests.Unit.Services;

public class PositionSizerEdgeCaseTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfig = new();
    private readonly PositionSizer _sut;

    public PositionSizerEdgeCaseTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfig.Object);
        // Use real YieldCalculator — no external dependencies
        _sut = new PositionSizer(_mockUow.Object, new YieldCalculator());
    }

    private static BotConfiguration MakeConfig(
        int breakevenHoursMax = 6,
        decimal totalCapital = 107m,
        decimal maxCapitalPerPos = 0.80m,
        int leverage = 5,
        decimal volumeFraction = 0.001m) => new()
    {
        TotalCapitalUsdc = totalCapital,
        MaxCapitalPerPosition = maxCapitalPerPos,
        DefaultLeverage = leverage,
        VolumeFraction = volumeFraction,
        MaxConcurrentPositions = 1,
        IsEnabled = true,
        OpenThreshold = 0.0003m,
        BreakevenHoursMax = breakevenHoursMax,
        UpdatedByUserId = "admin-user-id",
    };

    // ── Test 5: Break-even exceeds max → return 0 ─────────────────────────────

    [Fact]
    public async Task CalculateOptimalSize_ReturnsZero_WhenBreakevenExceedsMax()
    {
        // entryFeeRate = 0.001 - 0.0001 = 0.0009
        // breakEvenHours = 0.0009 / 0.0001 = 9 > BreakevenHoursMax=2 → 0
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(MakeConfig(breakevenHoursMax: 2));

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SpreadPerHour = 0.001m,
            NetYieldPerHour = 0.0001m,
            LongVolume24h = 100_000_000m,
            ShortVolume24h = 100_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().Be(0m);
    }

    // ── Test 6: Low fees → break-even passes → non-zero result ────────────────

    [Fact]
    public async Task CalculateOptimalSize_PassesBreakeven_WhenFeesLow()
    {
        // entryFeeRate = 0.001 - 0.0009 = 0.0001
        // breakEvenHours = 0.0001 / 0.0009 ≈ 0.11 < BreakevenHoursMax=6 → non-zero
        _mockBotConfig.Setup(b => b.GetActiveAsync())
            .ReturnsAsync(MakeConfig(breakevenHoursMax: 6));

        var opp = new ArbitrageOpportunityDto
        {
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SpreadPerHour = 0.001m,
            NetYieldPerHour = 0.0009m,
            LongVolume24h = 100_000_000m,
            ShortVolume24h = 100_000_000m,
            LongMarkPrice = 100m,
            ShortMarkPrice = 100m,
        };

        var result = await _sut.CalculateOptimalSizeAsync(opp);

        result.Should().BeGreaterThan(0m);
    }

    // ── Test 7: stepSize=0 falls back to decimal rounding ─────────────────────

    [Fact]
    public void RoundToStepSize_WithZeroStepSize_ReturnsRoundedToDecimals()
    {
        var result = PositionSizer.RoundToStepSize(1.23456m, 0m, 2);

        result.Should().Be(1.23m);
    }

    // ── Test 8: Large quantity floors correctly ────────────────────────────────

    [Fact]
    public void RoundToStepSize_WithLargeQuantity_FloorsCorrectly()
    {
        // floor(99.999 / 0.01) * 0.01 = floor(9999.9) * 0.01 = 9999 * 0.01 = 99.99
        var result = PositionSizer.RoundToStepSize(99.999m, 0.01m, 2);

        result.Should().Be(99.99m);
    }
}
