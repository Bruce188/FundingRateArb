using FluentAssertions;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.Logging.Abstractions;

namespace FundingRateArb.Tests.Unit.Infrastructure;

public class LighterWebSocketClientTests
{
    [Fact]
    public void KeepaliveInterval_IsUnder2Minutes()
    {
        LighterWebSocketClient.KeepaliveInterval.Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void MainnetUrl_IsCorrect()
    {
        LighterWebSocketClient.MainnetUrl.Should().Be("wss://mainnet.zklighter.elliot.ai/stream");
    }

    [Fact]
    public void IsConnected_IsFalse_BeforeConnect()
    {
        var client = new LighterWebSocketClient(NullLogger<LighterWebSocketClient>.Instance);
        client.IsConnected.Should().BeFalse();
    }
}
