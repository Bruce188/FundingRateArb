using FluentAssertions;
using FundingRateArb.Application.Services;

namespace FundingRateArb.Tests.Unit.Services;

public class HealthMonitorStateTests
{
    [Fact]
    public void DivergenceBreachCycles_OnConstruction_IsEmptyAndAcceptsOperations()
    {
        var state = new HealthMonitorState();

        state.DivergenceBreachCycles.Should().BeEmpty(
            "no positions have been tracked yet on a fresh state instance");

        // Add an entry
        state.DivergenceBreachCycles.TryAdd(1, 2).Should().BeTrue();
        state.DivergenceBreachCycles[1].Should().Be(2);

        // Update an entry
        state.DivergenceBreachCycles[1] = 3;
        state.DivergenceBreachCycles[1].Should().Be(3);

        // Remove an entry
        state.DivergenceBreachCycles.TryRemove(1, out var removed).Should().BeTrue();
        removed.Should().Be(3);
        state.DivergenceBreachCycles.Should().BeEmpty("after remove, dictionary should be empty again");
    }
}
