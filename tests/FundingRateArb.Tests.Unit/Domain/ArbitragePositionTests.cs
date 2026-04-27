using FluentAssertions;
using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Tests.Unit.Entities;

public class ArbitragePositionTests
{
    [Fact]
    public void IsPhantomFeeBackfill_DefaultValue_IsFalse()
    {
        var position = new ArbitragePosition();

        position.IsPhantomFeeBackfill.Should().BeFalse(
            "a freshly constructed entity must default to false so existing positions are unaffected by the backfill flag");
    }

    [Fact]
    public void DefaultConstructor_NewSlippageFields_AreNull()
    {
        var position = new ArbitragePosition();

        position.LongIntendedMidAtSubmit.Should().BeNull();
        position.ShortIntendedMidAtSubmit.Should().BeNull();
        position.LongEntrySlippagePct.Should().BeNull();
        position.ShortEntrySlippagePct.Should().BeNull();
        position.LongExitSlippagePct.Should().BeNull();
        position.ShortExitSlippagePct.Should().BeNull();
    }

    [Fact]
    public void DefaultConstructor_OrderAttemptCounters_AreZero()
    {
        var p = new ArbitragePosition();
        p.LongOrderAttemptN.Should().Be(0);
        p.ShortOrderAttemptN.Should().Be(0);
    }
}
