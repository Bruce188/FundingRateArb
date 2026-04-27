using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Configuration;

/// <summary>
/// Verifies that the five previously-unguarded fields in BotConfiguration
/// now carry [Range] attributes with the correct bounds, and that the
/// DataAnnotations Validator enforces those bounds at runtime.
/// </summary>
public class BotConfigurationRangeAttributeTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static RangeAttribute? GetRangeAttribute(string propertyName)
    {
        var prop = typeof(BotConfiguration).GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull($"BotConfiguration should have property '{propertyName}'");
        return prop!.GetCustomAttribute<RangeAttribute>();
    }

    private static IList<ValidationResult> Validate(BotConfiguration cfg)
    {
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(cfg);
        Validator.TryValidateObject(cfg, ctx, results, validateAllProperties: true);
        return results;
    }

    private static double ToDouble(object? value) =>
        Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static int ToInt32(object? value) =>
        Convert.ToInt32(value, CultureInfo.InvariantCulture);

    // -----------------------------------------------------------------------
    // OpenThreshold  [Range(0.0, 0.01)]
    // -----------------------------------------------------------------------

    [Fact]
    public void OpenThreshold_HasRangeAttribute()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.OpenThreshold));
        attr.Should().NotBeNull("OpenThreshold must carry a [Range] attribute");
    }

    [Fact]
    public void OpenThreshold_RangeAttribute_HasCorrectMinimum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.OpenThreshold));
        attr.Should().NotBeNull();
        ToDouble(attr!.Minimum).Should().Be(0.0,
            "OpenThreshold lower bound must be 0.0");
    }

    [Fact]
    public void OpenThreshold_RangeAttribute_HasCorrectMaximum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.OpenThreshold));
        attr.Should().NotBeNull();
        ToDouble(attr!.Maximum).Should().Be(0.01,
            "OpenThreshold upper bound must be 0.01");
    }

    [Theory]
    [InlineData(-0.0001)]   // below minimum
    [InlineData(0.011)]     // above maximum
    public void OpenThreshold_OutOfRange_FailsValidation(double value)
    {
        var cfg = ValidBase();
        cfg.OpenThreshold = (decimal)value;
        var results = Validate(cfg);
        results.Should().Contain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.OpenThreshold)),
            $"OpenThreshold={value} should fail [Range(0.0, 0.01)] validation");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0002)]    // legacy default
    [InlineData(0.0005)]    // current default
    [InlineData(0.01)]
    public void OpenThreshold_InRange_PassesValidation(double value)
    {
        var cfg = ValidBase();
        cfg.OpenThreshold = (decimal)value;
        var results = Validate(cfg);
        results.Should().NotContain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.OpenThreshold)),
            $"OpenThreshold={value} should pass [Range(0.0, 0.01)] validation");
    }

    // -----------------------------------------------------------------------
    // AlertThreshold  [Range(0.0, 0.01)]
    // -----------------------------------------------------------------------

    [Fact]
    public void AlertThreshold_HasRangeAttribute()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.AlertThreshold));
        attr.Should().NotBeNull("AlertThreshold must carry a [Range] attribute");
    }

    [Fact]
    public void AlertThreshold_RangeAttribute_HasCorrectMinimum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.AlertThreshold));
        attr.Should().NotBeNull();
        ToDouble(attr!.Minimum).Should().Be(0.0,
            "AlertThreshold lower bound must be 0.0");
    }

    [Fact]
    public void AlertThreshold_RangeAttribute_HasCorrectMaximum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.AlertThreshold));
        attr.Should().NotBeNull();
        ToDouble(attr!.Maximum).Should().Be(0.01,
            "AlertThreshold upper bound must be 0.01");
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(0.011)]
    public void AlertThreshold_OutOfRange_FailsValidation(double value)
    {
        var cfg = ValidBase();
        cfg.AlertThreshold = (decimal)value;
        var results = Validate(cfg);
        results.Should().Contain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.AlertThreshold)),
            $"AlertThreshold={value} should fail [Range(0.0, 0.01)] validation");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0001)]    // default
    [InlineData(0.01)]
    public void AlertThreshold_InRange_PassesValidation(double value)
    {
        var cfg = ValidBase();
        cfg.AlertThreshold = (decimal)value;
        var results = Validate(cfg);
        results.Should().NotContain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.AlertThreshold)),
            $"AlertThreshold={value} should pass [Range(0.0, 0.01)] validation");
    }

    // -----------------------------------------------------------------------
    // CloseThreshold  [Range(-0.01, 0.0)]
    // -----------------------------------------------------------------------

    [Fact]
    public void CloseThreshold_HasRangeAttribute()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.CloseThreshold));
        attr.Should().NotBeNull("CloseThreshold must carry a [Range] attribute");
    }

    [Fact]
    public void CloseThreshold_RangeAttribute_HasCorrectMinimum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.CloseThreshold));
        attr.Should().NotBeNull();
        ToDouble(attr!.Minimum).Should().Be(-0.01,
            "CloseThreshold lower bound must be -0.01");
    }

    [Fact]
    public void CloseThreshold_RangeAttribute_HasCorrectMaximum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.CloseThreshold));
        attr.Should().NotBeNull();
        ToDouble(attr!.Maximum).Should().Be(0.0,
            "CloseThreshold upper bound must be 0.0");
    }

    [Theory]
    [InlineData(-0.011)]    // below minimum
    [InlineData(0.0001)]    // above maximum (positive)
    public void CloseThreshold_OutOfRange_FailsValidation(double value)
    {
        var cfg = ValidBase();
        cfg.CloseThreshold = (decimal)value;
        var results = Validate(cfg);
        results.Should().Contain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.CloseThreshold)),
            $"CloseThreshold={value} should fail [Range(-0.01, 0.0)] validation");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-0.00005)]  // legacy default
    [InlineData(-0.0002)]   // current default
    [InlineData(0.0)]
    public void CloseThreshold_InRange_PassesValidation(double value)
    {
        var cfg = ValidBase();
        cfg.CloseThreshold = (decimal)value;
        var results = Validate(cfg);
        results.Should().NotContain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.CloseThreshold)),
            $"CloseThreshold={value} should pass [Range(-0.01, 0.0)] validation");
    }

    // -----------------------------------------------------------------------
    // MaxHoldTimeHours  [Range(1, 168)]
    // -----------------------------------------------------------------------

    [Fact]
    public void MaxHoldTimeHours_HasRangeAttribute()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.MaxHoldTimeHours));
        attr.Should().NotBeNull("MaxHoldTimeHours must carry a [Range] attribute");
    }

    [Fact]
    public void MaxHoldTimeHours_RangeAttribute_HasCorrectMinimum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.MaxHoldTimeHours));
        attr.Should().NotBeNull();
        ToInt32(attr!.Minimum).Should().Be(1,
            "MaxHoldTimeHours lower bound must be 1");
    }

    [Fact]
    public void MaxHoldTimeHours_RangeAttribute_HasCorrectMaximum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.MaxHoldTimeHours));
        attr.Should().NotBeNull();
        ToInt32(attr!.Maximum).Should().Be(168,
            "MaxHoldTimeHours upper bound must be 168 (one week)");
    }

    [Theory]
    [InlineData(0)]     // below minimum
    [InlineData(169)]   // above maximum
    public void MaxHoldTimeHours_OutOfRange_FailsValidation(int value)
    {
        var cfg = ValidBase();
        cfg.MaxHoldTimeHours = value;
        var results = Validate(cfg);
        results.Should().Contain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.MaxHoldTimeHours)),
            $"MaxHoldTimeHours={value} should fail [Range(1, 168)] validation");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(48)]    // default
    [InlineData(168)]
    public void MaxHoldTimeHours_InRange_PassesValidation(int value)
    {
        var cfg = ValidBase();
        cfg.MaxHoldTimeHours = value;
        var results = Validate(cfg);
        results.Should().NotContain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.MaxHoldTimeHours)),
            $"MaxHoldTimeHours={value} should pass [Range(1, 168)] validation");
    }

    // -----------------------------------------------------------------------
    // VolumeFraction  [Range(0.0001, 0.1)]
    // -----------------------------------------------------------------------

    [Fact]
    public void VolumeFraction_HasRangeAttribute()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.VolumeFraction));
        attr.Should().NotBeNull("VolumeFraction must carry a [Range] attribute");
    }

    [Fact]
    public void VolumeFraction_RangeAttribute_HasCorrectMinimum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.VolumeFraction));
        attr.Should().NotBeNull();
        ToDouble(attr!.Minimum).Should().Be(0.0001,
            "VolumeFraction lower bound must be 0.0001");
    }

    [Fact]
    public void VolumeFraction_RangeAttribute_HasCorrectMaximum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.VolumeFraction));
        attr.Should().NotBeNull();
        ToDouble(attr!.Maximum).Should().Be(0.1,
            "VolumeFraction upper bound must be 0.1");
    }

    [Theory]
    [InlineData(0.00009)]   // below minimum (< 0.0001)
    [InlineData(0.11)]      // above maximum
    public void VolumeFraction_OutOfRange_FailsValidation(double value)
    {
        var cfg = ValidBase();
        cfg.VolumeFraction = (decimal)value;
        var results = Validate(cfg);
        results.Should().Contain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.VolumeFraction)),
            $"VolumeFraction={value} should fail [Range(0.0001, 0.1)] validation");
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.001)]     // default
    [InlineData(0.1)]
    public void VolumeFraction_InRange_PassesValidation(double value)
    {
        var cfg = ValidBase();
        cfg.VolumeFraction = (decimal)value;
        var results = Validate(cfg);
        results.Should().NotContain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.VolumeFraction)),
            $"VolumeFraction={value} should pass [Range(0.0001, 0.1)] validation");
    }

    // -----------------------------------------------------------------------
    // Default values are within their respective ranges
    // -----------------------------------------------------------------------

    [Fact]
    public void DefaultBotConfiguration_AllFiveNewFields_AreWithinTheirRanges()
    {
        var cfg = new BotConfiguration();
        var results = Validate(cfg);

        var newFields = new[]
        {
            nameof(BotConfiguration.OpenThreshold),
            nameof(BotConfiguration.AlertThreshold),
            nameof(BotConfiguration.CloseThreshold),
            nameof(BotConfiguration.MaxHoldTimeHours),
            nameof(BotConfiguration.VolumeFraction),
        };

        foreach (var field in newFields)
        {
            results.Should().NotContain(r => r.MemberNames.Contains(field),
                $"default value for {field} must satisfy its [Range] constraint");
        }
    }

    // -----------------------------------------------------------------------
    // MaxAcceptableSlippagePct  [Range(0.0001, 0.01)]
    // -----------------------------------------------------------------------

    [Fact]
    public void MaxAcceptableSlippagePct_HasRangeAttribute()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.MaxAcceptableSlippagePct));
        attr.Should().NotBeNull("MaxAcceptableSlippagePct must carry a [Range] attribute");
    }

    [Fact]
    public void MaxAcceptableSlippagePct_RangeAttribute_HasCorrectMinimum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.MaxAcceptableSlippagePct));
        attr.Should().NotBeNull();
        ToDouble(attr!.Minimum).Should().Be(0.0001,
            "MaxAcceptableSlippagePct lower bound must be 0.0001");
    }

    [Fact]
    public void MaxAcceptableSlippagePct_RangeAttribute_HasCorrectMaximum()
    {
        var attr = GetRangeAttribute(nameof(BotConfiguration.MaxAcceptableSlippagePct));
        attr.Should().NotBeNull();
        ToDouble(attr!.Maximum).Should().Be(0.01,
            "MaxAcceptableSlippagePct upper bound must be 0.01");
    }

    [Theory]
    [InlineData(0.00009)]   // below minimum
    [InlineData(0.011)]     // above maximum
    public void MaxAcceptableSlippagePct_OutOfRange_FailsValidation(double value)
    {
        var cfg = ValidBase();
        cfg.MaxAcceptableSlippagePct = (decimal)value;
        var results = Validate(cfg);
        results.Should().Contain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.MaxAcceptableSlippagePct)),
            $"MaxAcceptableSlippagePct={value} should fail [Range(0.0001, 0.01)] validation");
    }

    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.001)]     // default
    [InlineData(0.01)]
    public void MaxAcceptableSlippagePct_InRange_PassesValidation(double value)
    {
        var cfg = ValidBase();
        cfg.MaxAcceptableSlippagePct = (decimal)value;
        var results = Validate(cfg);
        results.Should().NotContain(r =>
            r.MemberNames.Contains(nameof(BotConfiguration.MaxAcceptableSlippagePct)),
            $"MaxAcceptableSlippagePct={value} should pass [Range(0.0001, 0.01)] validation");
    }

    // -----------------------------------------------------------------------
    // Factory: a fully-valid BotConfiguration baseline
    // -----------------------------------------------------------------------

    private static BotConfiguration ValidBase() => new BotConfiguration
    {
        // five newly-guarded fields set to their current entity defaults
        OpenThreshold = 0.0005m,
        AlertThreshold = 0.0001m,
        CloseThreshold = -0.0002m,
        MaxHoldTimeHours = 48,
        VolumeFraction = 0.001m,
    };
}
