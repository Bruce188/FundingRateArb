using CryptoExchange.Net.Objects.Sockets;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using HyperLiquid.Net.Interfaces.Clients;
using HyperLiquid.Net.Objects.Models;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// HyperLiquid WebSocket market data stream. All interactions with the underlying
/// socket client and the tracked subscription list are serialized through
/// <see cref="_socketLock"/>. A rolling disconnect counter trips a cooldown
/// throttle when churn exceeds <see cref="DisconnectThreshold"/> events per minute,
/// preventing the runaway subscribe/unsubscribe cycle that preceded native crashes
/// in Azure production (2026-04-09 incident).
/// </summary>
public class HyperliquidMarketDataStream : IMarketDataStream
{
    // ── Constants ────────────────────────────────────────────────────────────────
    private const int DisconnectThreshold = 20;          // events per window
    private static readonly TimeSpan DisconnectWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ThrottleCooldown = TimeSpan.FromMinutes(1);

    // ── Dependencies ─────────────────────────────────────────────────────────────
    private readonly IHyperLiquidSocketClient _socketClient;
    private readonly IMarketDataCache _cache;
    private readonly ILogger<HyperliquidMarketDataStream> _logger;

    // ── Shared state (always accessed under _socketLock) ────────────────────────
    // We keep a plain List here intentionally — the semaphore is the single source
    // of mutual exclusion for subscribe / unsubscribe / disconnect bookkeeping.
    private readonly SemaphoreSlim _socketLock = new(1, 1);
    private readonly List<UpdateSubscription> _subscriptions = new();
    private readonly Queue<DateTime> _recentDisconnects = new();
    private DateTime? _throttleUntil;

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

    public bool IsConnected
    {
        get
        {
            _socketLock.Wait();
            try
            {
                return _subscriptions.Count > 0;
            }
            finally
            {
                _socketLock.Release();
            }
        }
    }

    public event Action<FundingRateDto>? OnRateUpdate;
    public event Action<string, string>? OnDisconnected;

    public async Task StartAsync(IEnumerable<string> symbols, CancellationToken ct)
    {
        foreach (var symbol in symbols)
        {
            await SubscribeOneAsync(symbol, ct).ConfigureAwait(false);
        }

        await _socketLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_subscriptions.Count == 0)
            {
                _logger.LogWarning("No HyperLiquid WebSocket subscriptions were established");
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private async Task SubscribeOneAsync(string symbol, CancellationToken ct)
    {
        // Check the throttle without taking the lock first — cheap fast path.
        if (IsThrottled())
        {
            _logger.LogWarning(
                "HyperLiquid subscribe for {Symbol} skipped — disconnect churn throttle active",
                symbol);
            return;
        }

        await _socketLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the lock to avoid racing with a disconnect storm.
            if (IsThrottled())
            {
                _logger.LogWarning(
                    "HyperLiquid subscribe for {Symbol} skipped — disconnect churn throttle active",
                    symbol);
                return;
            }

            try
            {
                var result = await _socketClient.FuturesApi.SubscribeToSymbolUpdatesAsync(
                    symbol,
                    update => HandleSymbolUpdate(symbol, update),
                    ct).ConfigureAwait(false);

                if (!result.Success)
                {
                    _logger.LogWarning(
                        "HyperLiquid WS subscription failed for {Symbol}: {Error}",
                        symbol, result.Error);
                    return;
                }

                var sub = result.Data;
                sub.ConnectionLost += () =>
                {
                    _logger.LogWarning("HyperLiquid WebSocket connection lost for {Symbol}", symbol);
                    _ = RecordDisconnectAsync(symbol, CancellationToken.None);
                    OnDisconnected?.Invoke(ExchangeName, $"Connection lost for {symbol}");
                };
                sub.ConnectionRestored += elapsed =>
                    _logger.LogInformation(
                        "HyperLiquid WebSocket reconnected for {Symbol} after {Elapsed}",
                        symbol, elapsed);

                _subscriptions.Add(sub);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to HyperLiquid WS for {Symbol}", symbol);
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private void HandleSymbolUpdate(string symbol, DataEvent<HyperLiquidFuturesTicker> update)
    {
        // The SDK invokes this callback on its own async state machine — any
        // exception escaping here tears down the socket's inner loop and may
        // leave the process in an undefined state. Swallow and log instead.
        try
        {
            var ticker = update.Data;
            var dto = new FundingRateDto
            {
                ExchangeName = ExchangeName,
                Symbol = symbol,
                RawRate = ticker.FundingRate ?? 0m,
                RatePerHour = ticker.FundingRate ?? 0m, // Already per-hour
                MarkPrice = ticker.MarkPrice,
                IndexPrice = ticker.OraclePrice ?? 0m,
                Volume24hUsd = ticker.NotionalVolume,
            };

            _cache.Update(dto);
            OnRateUpdate?.Invoke(dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HyperLiquid update handler failed for {Symbol}", symbol);
        }
    }

    /// <summary>
    /// Records a disconnect event in the rolling window and trips the churn
    /// throttle if the window's count exceeds <see cref="DisconnectThreshold"/>.
    /// </summary>
    internal async Task RecordDisconnectAsync(string symbol, CancellationToken ct)
    {
        await _socketLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            _recentDisconnects.Enqueue(now);

            // Prune entries older than the window.
            var cutoff = now - DisconnectWindow;
            while (_recentDisconnects.Count > 0 && _recentDisconnects.Peek() < cutoff)
            {
                _recentDisconnects.Dequeue();
            }

            if (_recentDisconnects.Count > DisconnectThreshold
                && (_throttleUntil is null || _throttleUntil <= now))
            {
                _throttleUntil = now + ThrottleCooldown;
                _logger.LogWarning(
                    "HyperLiquid disconnect rate {Count}/min exceeded threshold; " +
                    "pausing subscription churn until {Until:O}",
                    _recentDisconnects.Count, _throttleUntil);
            }
        }
        finally
        {
            _socketLock.Release();
        }
    }

    private bool IsThrottled()
    {
        var until = _throttleUntil;
        return until is not null && DateTime.UtcNow < until;
    }

    // ── Test hooks (internal; InternalsVisibleTo FundingRateArb.Tests.Unit) ──────

    internal Task RecordDisconnectForTestAsync(string symbol, CancellationToken ct)
        => RecordDisconnectAsync(symbol, ct);

    internal bool IsThrottledForTest => IsThrottled();

    internal void InvokeSymbolUpdateForTest(string symbol, DataEvent<HyperLiquidFuturesTicker> update)
        => HandleSymbolUpdate(symbol, update);

    public async Task StopAsync()
    {
        await _socketLock.WaitAsync().ConfigureAwait(false);
        List<UpdateSubscription> snapshot;
        try
        {
            snapshot = _subscriptions.ToList();
            _subscriptions.Clear();
        }
        finally
        {
            _socketLock.Release();
        }

        foreach (var sub in snapshot)
        {
            try
            {
                await sub.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HyperLiquid subscription close failed");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _socketLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
