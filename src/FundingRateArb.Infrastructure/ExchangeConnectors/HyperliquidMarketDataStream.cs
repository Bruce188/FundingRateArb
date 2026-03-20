using CryptoExchange.Net.Objects.Sockets;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using HyperLiquid.Net.Interfaces.Clients;
using HyperLiquid.Net.Objects.Models;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class HyperliquidMarketDataStream : IMarketDataStream
{
    private readonly IHyperLiquidSocketClient _socketClient;
    private readonly IMarketDataCache _cache;
    private readonly ILogger<HyperliquidMarketDataStream> _logger;
    private readonly List<UpdateSubscription> _subscriptions = new();

    public HyperliquidMarketDataStream(
        IHyperLiquidSocketClient socketClient,
        IMarketDataCache cache,
        ILogger<HyperliquidMarketDataStream> logger)
    {
        _socketClient = socketClient;
        _cache = cache;
        _logger = logger;
    }

    public string ExchangeName => "Hyperliquid";
    public bool IsConnected => _subscriptions.Any(s => s != null);
    public event Action<FundingRateDto>? OnRateUpdate;
    public event Action<string, string>? OnDisconnected;

    public async Task StartAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        foreach (var symbol in symbols)
        {
            try
            {
                var result = await _socketClient.FuturesApi.SubscribeToSymbolUpdatesAsync(
                    symbol,
                    update => HandleSymbolUpdate(symbol, update),
                    ct);

                if (!result.Success)
                {
                    _logger.LogWarning("HyperLiquid WS subscription failed for {Symbol}: {Error}",
                        symbol, result.Error);
                    continue;
                }

                var sub = result.Data;
                sub.ConnectionLost += () =>
                {
                    _logger.LogWarning("HyperLiquid WebSocket connection lost for {Symbol}", symbol);
                    OnDisconnected?.Invoke(ExchangeName, $"Connection lost for {symbol}");
                };
                sub.ConnectionRestored += elapsed =>
                    _logger.LogInformation("HyperLiquid WebSocket reconnected for {Symbol} after {Elapsed}", symbol, elapsed);

                _subscriptions.Add(sub);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to HyperLiquid WS for {Symbol}", symbol);
            }
        }

        if (_subscriptions.Count == 0)
            _logger.LogWarning("No HyperLiquid WebSocket subscriptions were established");
    }

    private void HandleSymbolUpdate(string symbol, DataEvent<HyperLiquidFuturesTicker> update)
    {
        var ticker = update.Data;
        var dto = new FundingRateDto
        {
            ExchangeName = ExchangeName,
            Symbol       = symbol,
            RawRate      = ticker.FundingRate ?? 0m,
            RatePerHour  = ticker.FundingRate ?? 0m, // Already per-hour
            MarkPrice    = ticker.MarkPrice,
            IndexPrice   = ticker.OraclePrice ?? 0m,
            Volume24hUsd = ticker.NotionalVolume,
        };

        _cache.Update(dto);
        OnRateUpdate?.Invoke(dto);
    }

    public async Task StopAsync()
    {
        foreach (var sub in _subscriptions)
            await sub.CloseAsync();
        _subscriptions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
