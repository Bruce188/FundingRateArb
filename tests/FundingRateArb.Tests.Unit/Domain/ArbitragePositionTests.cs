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
}
