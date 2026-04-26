using FluentAssertions;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Entities;

public class BotConfigurationDefaultsTests
{
    [Fact]
    public void NewBotConfiguration_HasExpectedDefaultValues()
    {
        var config = new BotConfiguration();

        config.OpenThreshold.Should().Be(0.0005m,
            "OpenThreshold default must be updated to 0.0005m");

        config.CloseThreshold.Should().Be(0.0002m,
            "CloseThreshold default must be updated to 0.0002m");

        config.MinHoldTimeHours.Should().Be(4,
            "MinHoldTimeHours default must be updated to 4");

        config.EmergencyCloseSpreadThreshold.Should().Be(-0.001m,
            "EmergencyCloseSpreadThreshold must remain unchanged at -0.001m");
    }
}
