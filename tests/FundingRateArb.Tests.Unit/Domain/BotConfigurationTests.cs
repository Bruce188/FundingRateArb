using FluentAssertions;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Entities;

public class BotConfigurationTests
{
    [Fact]
    public void OpenConfirmTimeoutSeconds_DefaultValue_IsThirty()
    {
        var config = new BotConfiguration();

        config.OpenConfirmTimeoutSeconds.Should().Be(30,
            "entity default must match migration default (30 seconds)");
    }

    [Fact]
    public void OpenConfirmTimeoutSeconds_SetToZero_Throws()
    {
        var config = new BotConfiguration();

        var act = () => config.OpenConfirmTimeoutSeconds = 0;

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("OpenConfirmTimeoutSeconds");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(int.MinValue)]
    public void OpenConfirmTimeoutSeconds_SetToNegative_Throws(int value)
    {
        var config = new BotConfiguration();

        var act = () => config.OpenConfirmTimeoutSeconds = value;

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("OpenConfirmTimeoutSeconds");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(300)]
    [InlineData(999)]
    public void OpenConfirmTimeoutSeconds_SetToPositive_Stores(int value)
    {
        var config = new BotConfiguration();

        config.OpenConfirmTimeoutSeconds = value;

        config.OpenConfirmTimeoutSeconds.Should().Be(value,
            "positive values must be accepted and stored as-is");
    }

    [Fact]
    public void BotConfiguration_NewInstance_HasExpectedDivergenceMonitoringDefaults()
    {
        var config = new BotConfiguration();

        config.DivergenceAlertConfirmationCycles.Should().Be(1,
            "default must be 1 to preserve existing test behavior (debounce is a no-op at N=1)");
        config.RotationDivergenceHorizonHours.Should().Be(2.0m,
            "default horizon of 2h is the configured production value");
        config.PreferCloseOnDivergenceNarrowing.Should().BeTrue(
            "soft close preference should be on by default");
    }
}
