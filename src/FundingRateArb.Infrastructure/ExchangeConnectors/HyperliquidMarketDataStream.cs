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
    // NB7 from review-v131: volatile counter backs IsConnected so the getter does NOT
    // acquire _socketLock synchronously. Calling IsConnected from /healthz must never
    // block on an in-flight subscribe (which itself awaits a WebSocket handshake under
    // the lock). Updated under _socketLock after every _subscriptions mutation.
    private volatile int _subscriptionCount;

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

    // NB7 from review-v131: lock-free read. `/healthz` calls this synchronously via
    // WebSocketStreamHealthCheck; the previous implementation blocked the calling thread
    // on `_socketLock.Wait()` during any in-flight subscribe, which could hang the health
    // endpoint for the duration of a HyperLiquid WebSocket handshake.
    public bool IsConnected => _subscriptionCount > 0;

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
                var result = await _socketClient.FuturesApi.ExchangeData.SubscribeToSymbolUpdatesAsync(
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
                // Keep _subscriptionCount in sync with the list so IsConnected is
                // lock-free and accurate.
                _subscriptionCount = _subscriptions.Count;
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

            // NB10 from review-v131: `>=` not `>` so the documented "20 events per minute"
            // threshold trips on the 20th event, matching the class XML comment. The
            // previous `>` required 21 events, contradicting the stated behaviour.
            if (_recentDisconnects.Count >= DisconnectThreshold
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
        // N7 from review-v131: StopAsync previously called `_socketLock.WaitAsync()`
        // with no token — if a subscribe was stuck inside the SDK during shutdown this
        // would hang the process forever. Use a short timeout CTS so Stop can proceed
        // to close whatever subscriptions it already has and let the SDK tear down.
        using var stopTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        List<UpdateSubscription> snapshot;
        try
        {
            await _socketLock.WaitAsync(stopTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "HyperLiquid StopAsync could not acquire socket lock within 5s — proceeding with best-effort close");
            // Best-effort snapshot without the lock — accepting that a concurrent
            // subscribe may mutate the list. Shutdown is imminent; consistency is less
            // important than progress.
            try
            {
                snapshot = _subscriptions.ToList();
            }
            catch (Exception)
            {
                // Torn-list read during concurrent write — fall back to an empty snapshot.
                snapshot = new List<UpdateSubscription>();
            }
            _subscriptions.Clear();
            _subscriptionCount = 0;
            goto closeSnapshot;
        }
        try
        {
            snapshot = _subscriptions.ToList();
            _subscriptions.Clear();
            _subscriptionCount = 0;
        }
        finally
        {
            _socketLock.Release();
        }

    closeSnapshot:
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
