using System.Reflection;
using FluentAssertions;
using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Tests.Unit.DTOs;

/// <summary>
/// Tests that ArbitrageOpportunityDto exposes the reference-interval projection fields
/// required for display and ranking (Task 1.4). These fields are display/ranking only —
/// never used for PnL calculation.
/// </summary>
public class ArbitrageOpportunityDtoReferenceIntervalTests
{
    private static readonly Type DtoType = typeof(ArbitrageOpportunityDto);

    [Fact]
    public void LongRateReferenceInterval_PropertyExists_AndIsDecimal()
    {
        var property = DtoType.GetProperty("LongRateReferenceInterval");

        property.Should().NotBeNull(
            "ArbitrageOpportunityDto must expose LongRateReferenceInterval for display/ranking");
        property!.PropertyType.Should().Be<decimal>(
            "LongRateReferenceInterval must be a non-nullable decimal");
    }

    [Fact]
    public void ShortRateReferenceInterval_PropertyExists_AndIsDecimal()
    {
        var property = DtoType.GetProperty("ShortRateReferenceInterval");

        property.Should().NotBeNull(
            "ArbitrageOpportunityDto must expose ShortRateReferenceInterval for display/ranking");
        property!.PropertyType.Should().Be<decimal>(
            "ShortRateReferenceInterval must be a non-nullable decimal");
    }

    [Fact]
    public void SpreadReferenceInterval_PropertyExists_AndIsDecimal()
    {
        var property = DtoType.GetProperty("SpreadReferenceInterval");

        property.Should().NotBeNull(
            "ArbitrageOpportunityDto must expose SpreadReferenceInterval for display/ranking");
        property!.PropertyType.Should().Be<decimal>(
            "SpreadReferenceInterval must be a non-nullable decimal");
    }

    [Fact]
    public void ReferenceIntervalHours_PropertyExists_AndIsInt()
    {
        var property = DtoType.GetProperty("ReferenceIntervalHours");

        property.Should().NotBeNull(
            "ArbitrageOpportunityDto must expose ReferenceIntervalHours so consumers know what interval was used");
        property!.PropertyType.Should().Be<int>(
            "ReferenceIntervalHours must be a non-nullable int representing hours (typically 8)");
    }

    [Fact]
    public void LongRateReferenceInterval_IsReadWrite()
    {
        var property = DtoType.GetProperty("LongRateReferenceInterval");

        property.Should().NotBeNull("LongRateReferenceInterval property must exist");
        property!.CanRead.Should().BeTrue("LongRateReferenceInterval must be readable");
        property.CanWrite.Should().BeTrue("LongRateReferenceInterval must be writable");

        var dto = new ArbitrageOpportunityDto();
        property.SetValue(dto, 0.0012m);
        property.GetValue(dto).Should().Be(0.0012m,
            "LongRateReferenceInterval must round-trip the assigned value");
    }

    [Fact]
    public void ShortRateReferenceInterval_IsReadWrite()
    {
        var property = DtoType.GetProperty("ShortRateReferenceInterval");

        property.Should().NotBeNull("ShortRateReferenceInterval property must exist");
        property!.CanRead.Should().BeTrue("ShortRateReferenceInterval must be readable");
        property.CanWrite.Should().BeTrue("ShortRateReferenceInterval must be writable");

        var dto = new ArbitrageOpportunityDto();
        property.SetValue(dto, 0.0008m);
        property.GetValue(dto).Should().Be(0.0008m,
            "ShortRateReferenceInterval must round-trip the assigned value");
    }

    [Fact]
    public void SpreadReferenceInterval_IsReadWrite()
    {
        var property = DtoType.GetProperty("SpreadReferenceInterval");

        property.Should().NotBeNull("SpreadReferenceInterval property must exist");
        property!.CanRead.Should().BeTrue("SpreadReferenceInterval must be readable");
        property.CanWrite.Should().BeTrue("SpreadReferenceInterval must be writable");

        var dto = new ArbitrageOpportunityDto();
        property.SetValue(dto, 0.0004m);
        property.GetValue(dto).Should().Be(0.0004m,
            "SpreadReferenceInterval must round-trip the assigned value");
    }

    [Fact]
    public void ReferenceIntervalHours_IsReadWrite()
    {
        var property = DtoType.GetProperty("ReferenceIntervalHours");

        property.Should().NotBeNull("ReferenceIntervalHours property must exist");
        property!.CanRead.Should().BeTrue("ReferenceIntervalHours must be readable");
        property.CanWrite.Should().BeTrue("ReferenceIntervalHours must be writable");

        var dto = new ArbitrageOpportunityDto();
        property.SetValue(dto, 8);
        property.GetValue(dto).Should().Be(8,
            "ReferenceIntervalHours must round-trip the assigned value; typical reference interval is 8 hours");
    }

    [Fact]
    public void ReferenceIntervalFields_DefaultToZero_NotNullable()
    {
        // All four fields are non-nullable — they default to the CLR zero on new instances.
        var dto = new ArbitrageOpportunityDto();

        var longProp = DtoType.GetProperty("LongRateReferenceInterval");
        var shortProp = DtoType.GetProperty("ShortRateReferenceInterval");
        var spreadProp = DtoType.GetProperty("SpreadReferenceInterval");
        var hoursProp = DtoType.GetProperty("ReferenceIntervalHours");

        longProp.Should().NotBeNull("LongRateReferenceInterval must exist");
        shortProp.Should().NotBeNull("ShortRateReferenceInterval must exist");
        spreadProp.Should().NotBeNull("SpreadReferenceInterval must exist");
        hoursProp.Should().NotBeNull("ReferenceIntervalHours must exist");

        longProp!.GetValue(dto).Should().Be(0m, "LongRateReferenceInterval defaults to 0");
        shortProp!.GetValue(dto).Should().Be(0m, "ShortRateReferenceInterval defaults to 0");
        spreadProp!.GetValue(dto).Should().Be(0m, "SpreadReferenceInterval defaults to 0");
        hoursProp!.GetValue(dto).Should().Be(0, "ReferenceIntervalHours defaults to 0");
    }

    [Fact]
    public void ExistingPerHourFields_AreNotRemoved()
    {
        // Regression guard: adding *ReferenceInterval fields must NOT remove any *PerHour field.
        DtoType.GetProperty("LongRatePerHour").Should().NotBeNull("LongRatePerHour must remain");
        DtoType.GetProperty("ShortRatePerHour").Should().NotBeNull("ShortRatePerHour must remain");
        DtoType.GetProperty("SpreadPerHour").Should().NotBeNull("SpreadPerHour must remain");
        DtoType.GetProperty("NetYieldPerHour").Should().NotBeNull("NetYieldPerHour must remain");
        DtoType.GetProperty("BoostedNetYieldPerHour").Should().NotBeNull("BoostedNetYieldPerHour must remain");
    }
}
