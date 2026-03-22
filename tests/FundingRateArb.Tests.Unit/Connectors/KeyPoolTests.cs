using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Moq;

namespace FundingRateArb.Tests.Unit.Connectors;

public class KeyPoolTests
{
    [Fact]
    public void RoundRobin_CyclesThroughAllConnectors()
    {
        var c1 = new Mock<IExchangeConnector>().Object;
        var c2 = new Mock<IExchangeConnector>().Object;
        var c3 = new Mock<IExchangeConnector>().Object;

        var pool = new ExchangeConnectorFactory.KeyPool([c1, c2, c3]);

        var results = new List<IExchangeConnector>();
        for (int i = 0; i < 6; i++)
            results.Add(pool.GetNext()!);

        // All three connectors should appear in the first 3 calls
        results.Take(3).Should().Contain(c1);
        results.Take(3).Should().Contain(c2);
        results.Take(3).Should().Contain(c3);
    }

    [Fact]
    public void GetNext_SkipsCooledDownConnector()
    {
        var c1 = new Mock<IExchangeConnector>().Object;
        var c2 = new Mock<IExchangeConnector>().Object;

        var pool = new ExchangeConnectorFactory.KeyPool([c1, c2]);
        pool.MarkCooldown(c1, TimeSpan.FromMinutes(5));

        // Should only return c2 since c1 is in cooldown
        var results = new HashSet<IExchangeConnector>();
        for (int i = 0; i < 4; i++)
        {
            var connector = pool.GetNext()!;
            if (!pool.IsInCooldown(connector))
                results.Add(connector);
        }

        results.Should().Contain(c2);
    }

    [Fact]
    public void GetNext_AllInCooldown_ReturnsSoonestExpiring()
    {
        var c1 = new Mock<IExchangeConnector>().Object;
        var c2 = new Mock<IExchangeConnector>().Object;

        var pool = new ExchangeConnectorFactory.KeyPool([c1, c2]);
        pool.MarkCooldown(c1, TimeSpan.FromMinutes(1));  // expires sooner
        pool.MarkCooldown(c2, TimeSpan.FromMinutes(10)); // expires later

        var result = pool.GetNext();

        // Should return c1 (shortest cooldown)
        result.Should().Be(c1);
    }

    [Fact]
    public void SingleConnector_BehavesLikeCurrentSystem()
    {
        var c1 = new Mock<IExchangeConnector>().Object;
        var pool = new ExchangeConnectorFactory.KeyPool([c1]);

        var result1 = pool.GetNext();
        var result2 = pool.GetNext();

        result1.Should().Be(c1);
        result2.Should().Be(c1);
    }

    [Fact]
    public void EmptyPool_ReturnsNull()
    {
        var pool = new ExchangeConnectorFactory.KeyPool([]);

        var result = pool.GetNext();

        result.Should().BeNull();
    }

    [Fact]
    public void MarkRateLimited_ThenExpires_ReturnsConnector()
    {
        var c1 = new Mock<IExchangeConnector>().Object;
        var pool = new ExchangeConnectorFactory.KeyPool([c1]);

        // Mark with zero cooldown (already expired)
        pool.MarkCooldown(c1, TimeSpan.Zero);

        var result = pool.GetNext();
        result.Should().Be(c1);
    }
}
