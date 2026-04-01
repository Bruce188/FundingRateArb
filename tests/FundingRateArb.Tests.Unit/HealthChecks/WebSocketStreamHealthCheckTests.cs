using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

namespace FundingRateArb.Tests.Unit.HealthChecks;

public class WebSocketStreamHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_AllConnected_ReturnsHealthy()
    {
        var stream1 = CreateMockStream("Exchange1", isConnected: true);
        var stream2 = CreateMockStream("Exchange2", isConnected: true);
        var sut = new WebSocketStreamHealthCheck(new[] { stream1.Object, stream2.Object });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_SomeDisconnected_ReturnsDegraded()
    {
        var stream1 = CreateMockStream("Exchange1", isConnected: true);
        var stream2 = CreateMockStream("Exchange2", isConnected: false);
        var sut = new WebSocketStreamHealthCheck(new[] { stream1.Object, stream2.Object });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("Exchange2");
    }

    [Fact]
    public async Task CheckHealthAsync_AllDisconnected_ReturnsUnhealthy()
    {
        var stream1 = CreateMockStream("Exchange1", isConnected: false);
        var stream2 = CreateMockStream("Exchange2", isConnected: false);
        var sut = new WebSocketStreamHealthCheck(new[] { stream1.Object, stream2.Object });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Exchange1").And.Contain("Exchange2");
    }

    [Fact]
    public async Task CheckHealthAsync_NoStreams_ReturnsHealthy()
    {
        var sut = new WebSocketStreamHealthCheck(Enumerable.Empty<IMarketDataStream>());

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    private static Mock<IMarketDataStream> CreateMockStream(string name, bool isConnected)
    {
        var mock = new Mock<IMarketDataStream>();
        mock.Setup(s => s.ExchangeName).Returns(name);
        mock.Setup(s => s.IsConnected).Returns(isConnected);
        return mock;
    }
}
