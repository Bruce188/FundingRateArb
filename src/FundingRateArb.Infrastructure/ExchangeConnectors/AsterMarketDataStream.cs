using Aster.Net.Interfaces.Clients;
using Aster.Net.Objects.Models;
using CryptoExchange.Net.Objects.Sockets;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class AsterMarketDataStream : IMarketDataStream
{
    private readonly IAsterSocketClient _socketClient;
    private readonly IMarketDataCache _cache;
    private readonly ILogger<AsterMarketDataStream> _logger;
    private UpdateSubscription? _subscription;

    public AsterMarketDataStream(
        IAsterSocketClient socketClient,
        IMarketDataCache cache,
        ILogger<AsterMarketDataStream> logger)
    {
        _socketClient = socketClient;
        _cache = cache;
        _logger = logger;
    }

    public string ExchangeName => "Aster";
    public bool IsConnected => _subscription is not null;
    public event Action<FundingRateDto>? OnRateUpdate;
    public event Action<string, string>? OnDisconnected;

    public async Task StartAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        // Subscribe to all-symbols mark price stream at 1-second interval
        var result = await _socketClient.FuturesApi.SubscribeToMarkPriceUpdatesAsync(
            updateInterval: 1000,
            onMessage: HandleMarkPriceUpdate,
            ct: ct);

        if (!result.Success)
            throw new InvalidOperationException($"Aster WS subscription failed: {result.Error}");

        _subscription = result.Data;
        _subscription.ConnectionLost += () =>
        {
            _logger.LogWarning("Aster WebSocket connection lost");
            OnDisconnected?.Invoke(ExchangeName, "Connection lost");
        };
        _subscription.ConnectionRestored += elapsed =>
            _logger.LogInformation("Aster WebSocket reconnected after {Elapsed}", elapsed);
    }

    private void HandleMarkPriceUpdate(DataEvent<AsterMarkPriceUpdate[]> update)
    {
        foreach (var item in update.Data)
        {
            var symbol = NormalizeSymbol(item.Symbol);
            var dto = new FundingRateDto
            {
                ExchangeName = ExchangeName,
                Symbol       = symbol,
                RawRate      = item.FundingRate ?? 0m,
                RatePerHour  = (item.FundingRate ?? 0m) / 4m, // 4h → per-hour
                MarkPrice    = item.MarkPrice,
                IndexPrice   = item.IndexPrice,
                Volume24hUsd = 0m, // Not available in mark price stream
            };

            _cache.Update(dto);
            OnRateUpdate?.Invoke(dto);
        }
    }

    private static string NormalizeSymbol(string symbol) =>
        symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
            ? symbol[..^4]
            : symbol;

    public async Task StopAsync()
    {
        if (_subscription is not null)
            await _subscription.CloseAsync();
        _subscription = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
