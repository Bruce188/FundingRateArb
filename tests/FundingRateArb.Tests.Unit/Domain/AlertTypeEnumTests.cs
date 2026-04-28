using FluentAssertions;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Domain;

public class AlertTypeEnumTests
{
    [Fact]
    public void AlertType_ShouldContain_OperationalWarning()
    {
        var names = Enum.GetNames<AlertType>();

        names.Should().Contain("OperationalWarning",
            "OperationalWarning must be added as a member of the AlertType enum per task 1.1");
    }

    [Fact]
    public void AlertType_OperationalWarning_ShouldBeDefined()
    {
        Enum.IsDefined(typeof(AlertType), "OperationalWarning").Should().BeTrue(
            "AlertType.OperationalWarning must be a valid, defined enum member");
    }

    [Theory]
    [InlineData("OpportunityDetected", 0)]
    [InlineData("SpreadWarning", 1)]
    [InlineData("SpreadCollapsed", 2)]
    [InlineData("PositionOpened", 3)]
    [InlineData("PositionClosed", 4)]
    [InlineData("LegFailed", 5)]
    [InlineData("BotError", 6)]
    [InlineData("MarginWarning", 7)]
    [InlineData("PriceFeedFailure", 8)]
    [InlineData("LeverageReduced", 9)]
    [InlineData("QuantityMismatch", 10)]
    [InlineData("PnlDivergence", 11)]
    [InlineData("ExchangeCircuitBreaker", 12)]
    public void AlertType_ExistingMembers_ShouldRetainTheirValues(string memberName, int expectedValue)
    {
        var parsed = Enum.Parse<AlertType>(memberName);

        ((int)parsed).Should().Be(expectedValue,
            $"existing AlertType member '{memberName}' must not be renumbered");
    }
}
