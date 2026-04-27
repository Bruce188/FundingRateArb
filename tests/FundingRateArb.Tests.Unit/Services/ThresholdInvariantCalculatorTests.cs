using FluentAssertions;
using FundingRateArb.Application.Services;

namespace FundingRateArb.Tests.Unit.Services;

public class ThresholdInvariantCalculatorTests
{
    [Fact]
    public void ComputeRequiredOpenFloor_AtNewDefaults_Returns0_00045()
    {
        var floor = ThresholdInvariantCalculator.ComputeRequiredOpenFloor(
            closeThreshold: -0.0002m, minHoldTimeHours: 4);
        floor.Should().Be(0.00045m);
    }

    [Fact]
    public void ComputeRequiredOpenFloor_ZeroMinHold_ReturnsMaxValue()
    {
        ThresholdInvariantCalculator.ComputeRequiredOpenFloor(-0.0002m, 0)
            .Should().Be(decimal.MaxValue);
    }

    [Fact]
    public void ComputeRequiredOpenFloor_NegativeMinHold_ReturnsMaxValue()
    {
        ThresholdInvariantCalculator.ComputeRequiredOpenFloor(-0.0002m, -1)
            .Should().Be(decimal.MaxValue);
    }

    [Fact]
    public void ComputeRequiredOpenFloor_UsesAbsoluteValueOfClose()
    {
        var withNegative = ThresholdInvariantCalculator.ComputeRequiredOpenFloor(-0.0002m, 4);
        var withPositive = ThresholdInvariantCalculator.ComputeRequiredOpenFloor(0.0002m, 4);
        withNegative.Should().Be(withPositive);
    }

    [Fact]
    public void IsViolated_AtNewDefaults_False()
    {
        ThresholdInvariantCalculator.IsViolated(
            openThreshold: 0.0005m, closeThreshold: -0.0002m, minHoldTimeHours: 4)
            .Should().BeFalse();
    }

    [Fact]
    public void IsViolated_OpenBelowFloor_True()
    {
        ThresholdInvariantCalculator.IsViolated(
            openThreshold: 0.0001m, closeThreshold: -0.0002m, minHoldTimeHours: 4)
            .Should().BeTrue();
    }

    [Fact]
    public void IsViolated_OpenAtFloor_False()
    {
        // Boundary: open == floor must NOT violate (≥, not >)
        ThresholdInvariantCalculator.IsViolated(
            openThreshold: 0.00045m, closeThreshold: -0.0002m, minHoldTimeHours: 4)
            .Should().BeFalse();
    }

    [Fact]
    public void EstimatedRoundTripFeeRate_HardcodedAt0_001()
    {
        ThresholdInvariantCalculator.EstimatedRoundTripFeeRate.Should().Be(0.001m);
    }
}
