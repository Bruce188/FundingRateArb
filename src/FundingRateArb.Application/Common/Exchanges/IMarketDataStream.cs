using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IMarketDataStream : IAsyncDisposable
{
    string ExchangeName { get; }
    bool IsConnected { get; }
    event Action<FundingRateDto>? OnRateUpdate;
    event Action<string, string>? OnDisconnected;
    Task StartAsync(IEnumerable<string> symbols, CancellationToken ct);
    Task StopAsync();
}
