using FluentAssertions;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Services;

public class ConfigValidatorTests
{
    private readonly ConfigValidator _sut = new();

    private static BotConfiguration ValidConfig() => new()
    {
        OpenThreshold = 0.0002m,
        AlertThreshold = 0.0001m,
        CloseThreshold = -0.00005m,
        FeeAmortizationHours = 12,
        RateStalenessMinutes = 15,
        MaxHoldTimeHours = 48,
        MinHoldTimeHours = 2,
        DefaultLeverage = 5,
        MaxLeverageCap = 50,
        MaxConcurrentPositions = 1,
        MaxCapitalPerPosition = 0.90m,
        AllocationTopN = 3,
        AllocationStrategy = AllocationStrategy.Concentrated,
        MinPositionSizeUsdc = 5m,
        DailyDrawdownPausePct = 0.08m,
        ConsecutiveLossPause = 3,
    };

    [Fact]
    public void ValidConfig_ReturnsValid()
    {
        var result = _sut.Validate(ValidConfig());

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void DefaultConfig_IsValid()
    {
        // Validates that BotConfiguration's default property values pass all
        // validation rules. Catches future default regressions (e.g., setting
        // FeeAmortizationHours = 50 but forgetting to update MaxHoldTimeHours).
        var defaultConfig = new BotConfiguration();

        var result = _sut.Validate(defaultConfig);

        result.IsValid.Should().BeTrue(
            "default BotConfiguration should pass validation, but got errors: {0}",
            string.Join("; ", result.Errors));
    }

    [Fact]
    public void OpenThreshold_LessOrEqualAlert_Invalid()
    {
        var config = ValidConfig();
        config.OpenThreshold = config.AlertThreshold; // OpenThreshold must be > AlertThreshold

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("OpenThreshold"));
    }

    [Fact]
    public void AlertThreshold_Zero_Invalid()
    {
        var config = ValidConfig();
        config.AlertThreshold = 0m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("AlertThreshold"));
    }

    [Fact]
    public void AlertThreshold_Negative_Invalid()
    {
        var config = ValidConfig();
        config.AlertThreshold = -0.0001m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("AlertThreshold"));
    }

    [Fact]
    public void CloseThreshold_BelowFloor_Invalid()
    {
        var config = ValidConfig();
        config.CloseThreshold = -0.002m; // below -0.001 floor

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("CloseThreshold"));
    }

    [Fact]
    public void CloseThreshold_AtFloor_Valid()
    {
        var config = ValidConfig();
        config.CloseThreshold = -0.001m; // exactly at floor, should be valid

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().NotContain(e => e.Contains(">= -0.001"));
    }

    [Fact]
    public void CloseThreshold_Zero_Valid()
    {
        var config = ValidConfig();
        config.CloseThreshold = 0m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void CloseThreshold_JustBelowFloor_Invalid()
    {
        var config = ValidConfig();
        config.CloseThreshold = -0.00101m; // just below floor

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("CloseThreshold"));
    }

    [Fact]
    public void FeeAmortizationHours_Zero_Invalid()
    {
        var config = ValidConfig();
        config.FeeAmortizationHours = 0;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("FeeAmortizationHours"));
    }

    [Fact]
    public void RateStalenessMinutes_Zero_Invalid()
    {
        var config = ValidConfig();
        config.RateStalenessMinutes = 0;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("RateStalenessMinutes"));
    }

    [Fact]
    public void FeeAmortization_ExceedsMaxHold_Invalid()
    {
        var config = ValidConfig();
        config.FeeAmortizationHours = 100;
        config.MaxHoldTimeHours = 72;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("FeeAmortizationHours"));
    }

    [Fact]
    public void Leverage_Above10_Invalid()
    {
        var config = ValidConfig();
        config.DefaultLeverage = 15;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Leverage") || e.Contains("leverage"));
    }

    [Fact]
    public void MaxPositions_LessThanTopN_NonConcentrated_Invalid()
    {
        var config = ValidConfig();
        config.MaxConcurrentPositions = 1;
        config.AllocationTopN = 3;
        config.AllocationStrategy = AllocationStrategy.EqualSpread;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("MaxConcurrentPositions"));
    }

    [Fact]
    public void MaxPositions_LessThanTopN_Concentrated_Valid()
    {
        var config = ValidConfig();
        config.MaxConcurrentPositions = 1;
        config.AllocationTopN = 3;
        config.AllocationStrategy = AllocationStrategy.Concentrated;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue("Concentrated strategy does not require MaxConcurrentPositions >= AllocationTopN");
    }

    [Fact]
    public void MinPositionSize_Zero_Invalid()
    {
        var config = ValidConfig();
        config.MinPositionSizeUsdc = 0m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("MinPositionSizeUsdc"));
    }

    [Fact]
    public void DailyDrawdown_Zero_Invalid()
    {
        var config = ValidConfig();
        config.DailyDrawdownPausePct = 0m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("DailyDrawdownPausePct"));
    }

    [Fact]
    public void DailyDrawdown_AboveOne_Invalid()
    {
        var config = ValidConfig();
        config.DailyDrawdownPausePct = 1.5m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("DailyDrawdownPausePct"));
    }

    // ── D7: New validation rules ────────────────────────────────────────────────

    [Fact]
    public void Validate_ConsecutiveLossPauseCountZero_Invalid()
    {
        var config = ValidConfig();
        config.ConsecutiveLossPause = 0;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ConsecutiveLossPauseCount"));
    }

    [Fact]
    public void Validate_CloseThresholdAboveAlertThreshold_Invalid()
    {
        var config = ValidConfig();
        config.CloseThreshold = 0.001m; // > AlertThreshold=0.0001
        config.AlertThreshold = 0.0001m;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("CloseThreshold"));
    }

    [Fact]
    public void Validate_LeverageZero_Invalid()
    {
        var config = ValidConfig();
        config.DefaultLeverage = 0;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("DefaultLeverage"));
    }

    [Fact]
    public void Validate_MaxHoldTimeZero_Invalid()
    {
        var config = ValidConfig();
        config.MaxHoldTimeHours = 0;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("MaxHoldTimeHours"));
    }

    [Fact]
    public void Validate_CapitalOverAllocation_Invalid()
    {
        var config = ValidConfig();
        config.MaxCapitalPerPosition = 0.8m;
        config.MaxConcurrentPositions = 3;
        config.AllocationStrategy = AllocationStrategy.EqualSpread; // non-Concentrated

        var result = _sut.Validate(config);

        // 0.8 * 3 = 2.4 > 1.5 → error
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("over-allocation"));
    }

    [Fact]
    public void Validate_MinHoldExceedsMaxHold_Invalid()
    {
        var config = ValidConfig();
        config.MinHoldTimeHours = 24;
        config.MaxHoldTimeHours = 12;

        var result = _sut.Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("MinHoldTimeHours"));
    }

    [Fact]
    public void Validate_MinHoldEqualsMaxHold_Valid()
    {
        var config = ValidConfig();
        config.MinHoldTimeHours = 48;
        config.MaxHoldTimeHours = 48;

        var result = _sut.Validate(config);

        result.Errors.Should().NotContain(e => e.Contains("MinHoldTimeHours"));
    }
}
