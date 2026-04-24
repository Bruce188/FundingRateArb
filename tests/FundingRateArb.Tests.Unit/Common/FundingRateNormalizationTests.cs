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

    // ── New overloads: ToReferenceIntervalRate ────────────────────────────────

    [Fact]
    public void ToReferenceIntervalRate_PerHour_Multiplies_By_Eight()
    {
        // ToReferenceIntervalRate(decimal ratePerHour) must return ratePerHour * ReferenceIntervalHours (8).
        var ratePerHour = 0.0001m;
        var result = FundingRateNormalization.ToReferenceIntervalRate(ratePerHour);
        result.Should().Be(ratePerHour * FundingRateNormalization.ReferenceIntervalHours,
            "a 1h-native rate scaled to the 8h reference interval is multiplied by 8");
    }

    [Fact]
    public void ToReferenceIntervalRate_RawWithHourlyInterval_EqualsRawTimesEight()
    {
        // intervalHours == 1 → rawRate * 8 / 1 = rawRate * 8
        var rawRate = 0.0001m;
        var result = FundingRateNormalization.ToReferenceIntervalRate(rawRate, 1);
        result.Should().Be(rawRate * FundingRateNormalization.ReferenceIntervalHours,
            "a 1h-native raw rate scaled to the 8h reference returns rawRate * 8");
    }

    [Fact]
    public void ToReferenceIntervalRate_RawWithEightHourInterval_IsIdentity()
    {
        // intervalHours == ReferenceIntervalHours → rawRate unchanged
        var rawRate = 0.0008m;
        var result = FundingRateNormalization.ToReferenceIntervalRate(rawRate, FundingRateNormalization.ReferenceIntervalHours);
        result.Should().Be(rawRate,
            "an 8h-native raw rate is already at the reference interval and must be returned unchanged");
    }

    [Fact]
    public void ToReferenceIntervalRate_RawWithFourHourInterval_ReturnsRawTimesTwo()
    {
        // intervalHours == 4 → rawRate * 8 / 4 = rawRate * 2
        var rawRate = 0.0004m;
        var result = FundingRateNormalization.ToReferenceIntervalRate(rawRate, 4);
        result.Should().Be(rawRate * 2,
            "a 4h-native raw rate scaled to the 8h reference is multiplied by 2");
    }

    [Fact]
    public void ToReferenceIntervalRate_RawWithZeroInterval_ReturnsRaw()
    {
        // intervalHours <= 0 → guard returns rawRate unchanged (prevents divide-by-zero)
        var rawRate = 0.0008m;
        var result = FundingRateNormalization.ToReferenceIntervalRate(rawRate, 0);
        result.Should().Be(rawRate,
            "a zero interval is an invalid input; the guard must return the raw rate unchanged");
    }

    [Fact]
    public void ToEightHourRate_MatchesToReferenceIntervalRate()
    {
        // ToEightHourRate must delegate to ToReferenceIntervalRate so both return identical results.
        var ratePerHour = 0.0001m;
        var viaLegacy = FundingRateNormalization.ToEightHourRate(ratePerHour);
        var viaNew = FundingRateNormalization.ToReferenceIntervalRate(ratePerHour);
        viaLegacy.Should().Be(viaNew,
            "ToEightHourRate is a back-compat wrapper and must produce the same result as ToReferenceIntervalRate(ratePerHour)");
    }
}
