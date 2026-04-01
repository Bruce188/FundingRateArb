using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Connectors;

public class LighterWebSocketClientTests
{
    [Fact]
    public void IsConnected_ReturnsFalse_WhenNotConnected()
    {
        var sut = new LighterWebSocketClient(NullLogger<LighterWebSocketClient>.Instance);

        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow_WhenNeverConnected()
    {
        var sut = new LighterWebSocketClient(NullLogger<LighterWebSocketClient>.Instance);

        var act = () => sut.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow_WhenCalledTwice()
    {
        var sut = new LighterWebSocketClient(NullLogger<LighterWebSocketClient>.Instance);

        await sut.DisposeAsync();

        var act = () => sut.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync();
    }
}
