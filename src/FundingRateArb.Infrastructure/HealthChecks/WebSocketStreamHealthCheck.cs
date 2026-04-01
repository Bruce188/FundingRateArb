using FundingRateArb.Application.Common.Exchanges;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FundingRateArb.Infrastructure.HealthChecks;

public class WebSocketStreamHealthCheck : IHealthCheck
{
    private readonly IReadOnlyList<IMarketDataStream> _streams;

    public WebSocketStreamHealthCheck(IEnumerable<IMarketDataStream> streams)
    {
        _streams = streams.ToList().AsReadOnly();
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (_streams.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No WebSocket streams configured"));
        }

        var connected = new List<string>();
        var disconnected = new List<string>();
        foreach (var stream in _streams)
        {
            if (stream.IsConnected)
            {
                connected.Add(stream.ExchangeName);
            }
            else
            {
                disconnected.Add(stream.ExchangeName);
            }
        }

        if (disconnected.Count == 0)
        {
            var streamWord = connected.Count == 1 ? "stream" : "streams";
            return Task.FromResult(HealthCheckResult.Healthy($"All {connected.Count} {streamWord} connected"));
        }

        if (connected.Count == 0)
        {
            var streamWord = disconnected.Count == 1 ? "stream" : "streams";
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"All {disconnected.Count} {streamWord} disconnected: {string.Join(", ", disconnected)}"));
        }

        var degradedWord = disconnected.Count == 1 ? "stream" : "streams";
        return Task.FromResult(HealthCheckResult.Degraded(
            $"{disconnected.Count} {degradedWord} disconnected: {string.Join(", ", disconnected)}"));
    }
}
