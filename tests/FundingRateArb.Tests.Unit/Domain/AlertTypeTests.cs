using FluentAssertions;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Domain;

public class AlertTypeTests
{
    [Fact]
    public void HighSlippageWarning_IsDefined()
    {
        Enum.IsDefined(typeof(AlertType), AlertType.HighSlippageWarning).Should().BeTrue();
    }

    [Fact]
    public void OperationalWarning_StillDefined()
    {
        // Regression: ensure OperationalWarning was not accidentally removed when adding the new value.
        Enum.IsDefined(typeof(AlertType), AlertType.OperationalWarning).Should().BeTrue();
    }
}
