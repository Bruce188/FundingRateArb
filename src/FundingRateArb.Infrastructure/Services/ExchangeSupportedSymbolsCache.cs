using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Aster.Net.Interfaces.Clients;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Infrastructure.ExchangeConnectors.Models;
using HyperLiquid.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Singleton cache that holds the set of tradeable perpetual symbols per exchange.
/// Each entry has a 30-minute TTL; on expiry the metadata endpoint is re-queried.
/// On upstream failure the last-known-good set is returned (empty set if never loaded).
/// </summary>
public sealed class ExchangeSupportedSymbolsCache : IExchangeSupportedSymbolsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    // N4: single source of truth for supported exchange names (used by both
    //     LoadSymbolsAsync dispatch and RefreshAsync iteration).
    private static readonly string[] SupportedExchangeNames = ["Hyperliquid", "Aster", "Lighter"];

    // N3: time provider — injected via constructor; defaults to wall-clock.
    //     nit4: the setter is intentionally internal (test-only mutable via InternalsVisibleTo).
    //     Production code never reassigns it; only test helpers use the setter after construction.
    private Func<DateTime> _timeProvider;

    /// <summary>Test seam — mutable via InternalsVisibleTo; not accessible at runtime via DI.</summary>
    internal Func<DateTime> TimeProvider
    {
        get => _timeProvider;
        set => _timeProvider = value;
    }

    private sealed class CacheEntry
    {
        // nit7: typed as IReadOnlySet so the immutability is structural within the assembly.
        public IReadOnlySet<string> Symbols { get; }
        public DateTime LoadedAtUtc { get; }

        public CacheEntry(IEnumerable<string> symbols, DateTime loadedAtUtc)
        {
            Symbols = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
            LoadedAtUtc = loadedAtUtc;
        }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    // N1/NB1: pre-populated from SupportedExchangeNames in the constructor so cardinality is
    //         strictly bounded to three entries — unknown caller names short-circuit via TryGetValue.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHyperLiquidRestClient _hyperLiquidClient;
    private readonly IAsterRestClient _asterClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExchangeSupportedSymbolsCache> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public ExchangeSupportedSymbolsCache(
        IHyperLiquidRestClient hyperLiquidClient,
        IAsterRestClient asterClient,
        IHttpClientFactory httpClientFactory,
        ILogger<ExchangeSupportedSymbolsCache> logger)
        : this(hyperLiquidClient, asterClient, httpClientFactory, logger, () => DateTime.UtcNow)
    {
    }

    // N3: internal constructor overload for DI or test injection of a time provider.
    internal ExchangeSupportedSymbolsCache(
        IHyperLiquidRestClient hyperLiquidClient,
        IAsterRestClient asterClient,
        IHttpClientFactory httpClientFactory,
        ILogger<ExchangeSupportedSymbolsCache> logger,
        Func<DateTime> timeProvider)
    {
        _hyperLiquidClient = hyperLiquidClient;
        _asterClient = asterClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _timeProvider = timeProvider;

        // NB1: pre-populate so the lock dictionary is bounded to three known exchanges.
        foreach (var name in SupportedExchangeNames)
            _locks[name] = new SemaphoreSlim(1, 1);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> GetSupportedSymbolsAsync(string exchangeName, CancellationToken ct = default)
    {
        var now = _timeProvider();

        if (_cache.TryGetValue(exchangeName, out var entry) && now - entry.LoadedAtUtc < Ttl)
        {
            // N2: return a read-only view to prevent callers from mutating the cached set.
            return entry.Symbols;
        }

        // NB1: _locks is pre-populated in the constructor; unknown names return empty immediately
        //      without allocating a new SemaphoreSlim.
        if (!_locks.TryGetValue(exchangeName, out var semaphore))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock (double-checked locking)
            now = _timeProvider();
            if (_cache.TryGetValue(exchangeName, out entry) && now - entry.LoadedAtUtc < Ttl)
            {
                return entry.Symbols;
            }

            var symbols = await LoadSymbolsAsync(exchangeName, ct).ConfigureAwait(false);
            if (symbols is not null)
            {
                // nit9: capture now once after the fetch completes to avoid double-call drift.
                var loadedAt = _timeProvider();
                var newEntry = new CacheEntry(symbols, loadedAt);
                _cache[exchangeName] = newEntry;
                return newEntry.Symbols;
            }
            else
            {
                // Upstream failure — return last-known-good or empty set
                if (_cache.TryGetValue(exchangeName, out var stale))
                {
                    // nit9: single call to _timeProvider() for age computation.
                    var age = _timeProvider() - stale.LoadedAtUtc;
                    _logger.LogWarning(
                        "Failed to refresh supported symbols for {Exchange} — using last-known-good ({Count} symbols, age {Age})",
                        exchangeName, stale.Symbols.Count, age);
                    // N2: return a read-only view
                    return stale.Symbols;
                }

                _logger.LogWarning(
                    "Failed to load supported symbols for {Exchange} on first attempt — returning empty set",
                    exchangeName);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // N4: iterate from SupportedExchangeNames — single source of truth
        foreach (var exchange in SupportedExchangeNames)
        {
            try
            {
                var semaphore = _locks.GetOrAdd(exchange, _ => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var symbols = await LoadSymbolsAsync(exchange, ct).ConfigureAwait(false);
                    if (symbols is not null)
                    {
                        // nit9: single _timeProvider() call after fetch completes.
                        _cache[exchange] = new CacheEntry(symbols, _timeProvider());
                        _logger.LogInformation("Refreshed supported symbols for {Exchange}: {Count} symbols",
                            exchange, symbols.Count);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to refresh supported symbols for {Exchange}", exchange);
            }
        }
    }

    private async Task<List<string>?> LoadSymbolsAsync(string exchangeName, CancellationToken ct)
    {
        try
        {
            // N4: dispatch must stay in sync with SupportedExchangeNames above
            return exchangeName switch
            {
                "Hyperliquid" => await LoadHyperliquidSymbolsAsync(ct).ConfigureAwait(false),
                "Aster" => await LoadAsterSymbolsAsync(ct).ConfigureAwait(false),
                "Lighter" => await LoadLighterSymbolsAsync(ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception loading supported symbols for {Exchange}", exchangeName);
            return null;
        }
    }

    private async Task<List<string>?> LoadHyperliquidSymbolsAsync(CancellationToken ct)
    {
        var result = await _hyperLiquidClient.FuturesApi.ExchangeData
            .GetExchangeInfoAndTickersAsync(ct)
            .ConfigureAwait(false);

        if (!result.Success || result.Data?.Tickers is null)
        {
            _logger.LogWarning("Hyperliquid metadata fetch failed: {Error}", result.Error);
            return null;
        }

        return result.Data.Tickers
            .Select(t => t.Symbol)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private async Task<List<string>?> LoadAsterSymbolsAsync(CancellationToken ct)
    {
        var result = await _asterClient.FuturesApi.ExchangeData
            .GetExchangeInfoAsync(ct)
            .ConfigureAwait(false);

        if (!result.Success || result.Data?.Symbols is null)
        {
            _logger.LogWarning("Aster metadata fetch failed: {Error}", result.Error);
            return null;
        }

        return result.Data.Symbols
            .Select(s => s.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
    }

    private async Task<List<string>?> LoadLighterSymbolsAsync(CancellationToken ct)
    {
        // NB2: base address is configured on the named client in DI (Program.cs).
        // NB3: MaxResponseContentBufferSize and Timeout are also set on the named client in DI.
        var client = _httpClientFactory.CreateClient("LighterMetadata");

        // NB3: deterministic disposal of the response message via `using`.
        // NB2: catch HttpRequestException (HTTP failure), JsonException (malformed body),
        //      and TaskCanceledException where it is NOT from the caller token (request timeout)
        //      so all three propagate to the last-known-good fallback rather than throwing.
        using var response = await client.GetAsync("orderBookDetails?filter=perp", ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            return null;
        }

        try
        {
            var details = await response.Content
                .ReadFromJsonAsync<LighterOrderBookDetailsResponse>(JsonOptions, ct)
                .ConfigureAwait(false);

            if (details?.OrderBookDetails is null)
            {
                return null;
            }

            return details.OrderBookDetails
                .Select(m => m.Symbol)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Request timeout (not caller cancellation) — treat as upstream failure.
            _logger.LogWarning(ex, "Lighter metadata request timed out");
            return null;
        }
    }
}
