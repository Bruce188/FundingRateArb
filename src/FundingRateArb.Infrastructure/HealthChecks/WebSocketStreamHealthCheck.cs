using FundingRateArb.Application.Common.Exchanges;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FundingRateArb.Infrastructure.HealthChecks;

public class WebSocketStreamHealthCheck : IHealthCheck
{
    private readonly IEnumerable<IMarketDataStream> _streams;

    public WebSocketStreamHealthCheck(IEnumerable<IMarketDataStream> streams)
    {
        _streams = streams;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var streamList = _streams.ToList();
        if (streamList.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No WebSocket streams configured"));
        }

        var connected = streamList.Where(s => s.IsConnected).Select(s => s.ExchangeName).ToList();
        var disconnected = streamList.Where(s => !s.IsConnected).Select(s => s.ExchangeName).ToList();

        if (disconnected.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy($"All {connected.Count} streams connected"));
        }

        if (connected.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"All {disconnected.Count} streams disconnected: {string.Join(", ", disconnected)}"));
        }

        return Task.FromResult(HealthCheckResult.Degraded(
            $"{disconnected.Count} stream(s) disconnected: {string.Join(", ", disconnected)}"));
    }
}
