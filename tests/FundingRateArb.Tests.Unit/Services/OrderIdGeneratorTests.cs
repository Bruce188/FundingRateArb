using FluentAssertions;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Services;

public class OrderIdGeneratorTests
{
    [Fact]
    public void For_SmokeTest_ReturnsExpectedFormat()
    {
        OrderIdGenerator.For(1, Side.Long, 1).Should().Be("frb-1-l-1");
    }

    [Fact]
    public void For_SameInputs_ReturnsSameString()
    {
        OrderIdGenerator.For(42, Side.Long, 1)
            .Should().Be(OrderIdGenerator.For(42, Side.Long, 1));
    }

    [Fact]
    public void For_DifferentPositionId_ReturnsDifferentString()
    {
        OrderIdGenerator.For(42, Side.Long, 1)
            .Should().NotBe(OrderIdGenerator.For(43, Side.Long, 1));
    }

    [Fact]
    public void For_DifferentSide_ReturnsDifferentString()
    {
        OrderIdGenerator.For(42, Side.Long, 1)
            .Should().NotBe(OrderIdGenerator.For(42, Side.Short, 1));
    }

    [Fact]
    public void For_DifferentAttemptN_ReturnsDifferentString()
    {
        OrderIdGenerator.For(42, Side.Long, 1)
            .Should().NotBe(OrderIdGenerator.For(42, Side.Long, 2));
    }

    [Theory]
    [InlineData(1, Side.Long, 1, "frb-1-l-1")]
    [InlineData(255, Side.Short, 5, "frb-ff-s-5")]
    [InlineData(4096, Side.Long, 1, "frb-1000-l-1")]
    public void For_FormatSpec_MatchesPattern(int positionId, Side side, int attemptN, string expected)
    {
        OrderIdGenerator.For(positionId, side, attemptN).Should().Be(expected);
    }

    [Fact]
    public void For_BotPrefixIsLowercase()
    {
        OrderIdGenerator.For(42, Side.Long, 1).Should().StartWith("frb-");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void For_PositionIdNonPositive_Throws(int positionId)
    {
        var act = () => OrderIdGenerator.For(positionId, Side.Long, 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void For_AttemptNNonPositive_Throws(int attemptN)
    {
        var act = () => OrderIdGenerator.For(42, Side.Long, attemptN);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void For_GeneratedTooLong_Throws()
    {
        // int.MaxValue in lowercase hex = "7fffffff" (8 chars).
        // "frb-" (4) + "7fffffff" (8) + "-l-" (3) = 15 chars. Need attemptN long enough to push past 24.
        // attemptN = 9 digits ("999999999") yields 24 chars total — boundary, still valid.
        // attemptN = 10 digits ("1000000000") yields 25 chars — must throw.
        var act = () => OrderIdGenerator.For(int.MaxValue, Side.Long, 1_000_000_000);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IsBotPrefixed_WithBotPrefix_ReturnsTrue()
    {
        OrderIdGenerator.IsBotPrefixed("frb-1-l-1").Should().BeTrue();
    }

    [Theory]
    [InlineData("foo-1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("fr-")]
    [InlineData("frb")]
    public void IsBotPrefixed_WithoutBotPrefix_ReturnsFalse(string? candidate)
    {
        OrderIdGenerator.IsBotPrefixed(candidate).Should().BeFalse();
    }

    [Theory]
    [InlineData("frb-1-l-1", true)]
    [InlineData("frb-deadbeef-s-3", true)]
    [InlineData("hl-7-l-2", false)]
    [InlineData("user-coid", false)]
    public void IsBotPrefixed_OrphanScenario_DetectsBotOrders(string candidate, bool expected)
    {
        OrderIdGenerator.IsBotPrefixed(candidate).Should().Be(expected);
    }
}
