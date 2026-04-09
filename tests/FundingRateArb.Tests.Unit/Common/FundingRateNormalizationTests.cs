using FluentAssertions;
using FundingRateArb.Application.Common;

namespace FundingRateArb.Tests.Unit.Common;

public class FundingRateNormalizationTests
{
    [Theory]
    [InlineData(0.0001, 0.0008)]
    [InlineData(0, 0)]
    [InlineData(-0.0001, -0.0008)]
    [InlineData(0.001, 0.008)]
    public void ToEightHourRate_MultipliesByEight(double ratePerHour, double expected)
    {
        var result = FundingRateNormalization.ToEightHourRate((decimal)ratePerHour);
        result.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData(0.0008, 8, 0.0001)]
    [InlineData(0.0001, 1, 0.0001)]
    [InlineData(0.0004, 4, 0.0001)]
    [InlineData(0, 8, 0)]
    [InlineData(-0.0008, 8, -0.0001)]
    public void ToPerHourRate_DividesByInterval(double rawRate, int intervalHours, double expected)
    {
        var result = FundingRateNormalization.ToPerHourRate((decimal)rawRate, intervalHours);
        result.Should().Be((decimal)expected);
    }

    [Fact]
    public void ToPerHourRate_ZeroInterval_ReturnsRawRateUnchanged()
    {
        var result = FundingRateNormalization.ToPerHourRate(0.0008m, 0);
        result.Should().Be(0.0008m,
            "guard prevents divide-by-zero; raw rate is returned as-is");
    }

    [Fact]
    public void ToPerHourRate_NegativeInterval_ReturnsRawRateUnchanged()
    {
        var result = FundingRateNormalization.ToPerHourRate(0.0008m, -1);
        result.Should().Be(0.0008m);
    }

    [Theory]
    [InlineData(0.0001, 0.876)]
    [InlineData(0, 0)]
    [InlineData(0.001, 8.76)]
    public void ToAnnualizedRate_MultipliesByHoursPerYear(double ratePerHour, double expected)
    {
        var result = FundingRateNormalization.ToAnnualizedRate((decimal)ratePerHour);
        result.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData(8, 1095)]    // 8760 / 8
    [InlineData(1, 8760)]
    [InlineData(4, 2190)]    // 8760 / 4
    [InlineData(24, 365)]
    public void CyclesPerYear_ComputesCorrectly(int intervalHours, int expected)
    {
        var result = FundingRateNormalization.CyclesPerYear(intervalHours);
        result.Should().Be(expected);
    }

    [Fact]
    public void CyclesPerYear_ZeroInterval_ReturnsZero()
    {
        FundingRateNormalization.CyclesPerYear(0).Should().Be(0m);
    }

    [Fact]
    public void CyclesPerYear_NegativeInterval_ReturnsZero()
    {
        FundingRateNormalization.CyclesPerYear(-1).Should().Be(0m);
    }

    [Fact]
    public void ReferenceIntervalHours_IsEight()
    {
        // Pins the constant so an accidental change is caught.
        FundingRateNormalization.ReferenceIntervalHours.Should().Be(8);
    }

    [Fact]
    public void HoursPerYear_IsStandardYear()
    {
        FundingRateNormalization.HoursPerYear.Should().Be(24 * 365);
    }

    [Fact]
    public void ToEightHourRate_UsesReferenceIntervalConstant()
    {
        // Sanity: multiplying by ReferenceIntervalHours matches the method output.
        var rate = 0.0001m;
        FundingRateNormalization.ToEightHourRate(rate)
            .Should().Be(rate * FundingRateNormalization.ReferenceIntervalHours);
    }
}
