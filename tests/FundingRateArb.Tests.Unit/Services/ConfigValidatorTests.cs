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

    // ── Gap-invariant tests ────────────────────────────────────────────────────

    [Fact]
    public void GapInvariant_DefaultConfig_NoWarning()
    {
        // BotConfiguration defaults (Task 1.2): Open=0.0005, Close=0.0002, MinHold=4
        // fee/hold = 0.001/4 = 0.00025; Close+fee/hold = 0.00045; Open=0.0005 >= 0.00045 → pass
        var result = _sut.Validate(new BotConfiguration());

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().BeNullOrEmpty();
    }

    [Fact]
    public void GapInvariant_ExactBoundary_NoWarning()
    {
        // Open == Close + fee/hold exactly → >= is satisfied, no warning
        var config = ValidConfig();
        config.MinHoldTimeHours = 4;
        config.CloseThreshold = 0.00005m; // < AlertThreshold → no CloseThreshold error
        // Open = 0.00005 + 0.001/4 = 0.00005 + 0.00025 = 0.0003
        config.OpenThreshold = 0.0003m;
        config.AlertThreshold = 0.0002m; // keep Open > Alert

        var result = _sut.Validate(config);

        result.Warnings.Should().BeNullOrEmpty();
    }

    [Fact]
    public void GapInvariant_OpenBelowMinRequired_Warning()
    {
        // Open < Close + fee/hold → warning (not an error — IsValid stays true)
        var config = ValidConfig();
        config.MinHoldTimeHours = 4;
        config.CloseThreshold = 0.00005m; // < AlertThreshold → no CloseThreshold error
        // minSpread = 0.00005 + 0.001/4 = 0.0003; set Open just below it
        config.OpenThreshold = 0.00025m;
        config.AlertThreshold = 0.0002m; // keep Open > Alert

        var result = _sut.Validate(config);

        result.IsValid.Should().BeTrue("gap invariant is a warning, not an error");
        result.Warnings.Should().NotBeNullOrEmpty();
        result.Warnings!.Should().Contain(w => w.Contains("OpenThreshold"));
    }

    [Fact]
    public void GapInvariant_MinHoldZero_WarningSupressed()
    {
        // When MinHoldTimeHours == 0, dividing by zero is undefined.
        // The check is skipped entirely — no warning is emitted.
        var config = ValidConfig();
        config.MinHoldTimeHours = 0;
        config.MaxHoldTimeHours = 48; // avoid unrelated MinHold > MaxHold error

        var result = _sut.Validate(config);

        result.Warnings.Should().BeNullOrEmpty(
            "the gap-invariant check is suppressed when MinHoldTimeHours == 0 to avoid divide-by-zero");
    }
}
