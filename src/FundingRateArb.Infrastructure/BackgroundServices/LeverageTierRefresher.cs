using System.Diagnostics;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

/// <summary>
/// Outcome of a per-exchange leverage-tier refresh attempt within a single cycle.
/// </summary>
internal enum RefreshOutcome
{
    Success,
    TransientFailure,
    CredentialFailure,
    ConnectorUnavailable,
}

/// <summary>
/// Background service that pre-fetches leverage tier data for all active (exchange, asset)
/// pairs on startup and refreshes every 30 minutes. Prevents the first trade opportunity
/// from opening against cold cache state where the effective leverage cap would fall back
/// to user config without bracket awareness.
/// </summary>
public class LeverageTierRefresher : BackgroundService
{
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Known credential-error message fragments. Declared as an array because the lookup
    /// is a linear substring scan (Contains) — a HashSet's hash/equality semantics would
    /// provide no benefit. Declared static readonly so the allocation occurs once per process.
    /// </summary>
    private static readonly (string Fragment, StringComparison Comparison)[] _knownCredentialErrors =
    {
        ("Invalid API-key",          StringComparison.OrdinalIgnoreCase),
        ("-2015",                     StringComparison.Ordinal),
        ("Unauthorized",             StringComparison.OrdinalIgnoreCase),
        ("credentials not provided", StringComparison.OrdinalIgnoreCase),
    };

    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _refreshInterval;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeverageTierRefresher> _logger;

    public LeverageTierRefresher(
        IServiceScopeFactory scopeFactory,
        ILogger<LeverageTierRefresher> logger)
        : this(scopeFactory, logger, DefaultInitialDelay, DefaultRefreshInterval)
    {
    }

    /// <summary>
    /// Test seam: lets unit tests inject short delays so the cycle completes quickly.
    /// Production registration uses the default-delay constructor above.
    /// </summary>
    internal LeverageTierRefresher(
        IServiceScopeFactory scopeFactory,
        ILogger<LeverageTierRefresher> logger,
        TimeSpan initialDelay,
        TimeSpan refreshInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refreshInterval, TimeSpan.Zero);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _initialDelay = initialDelay;
        _refreshInterval = refreshInterval;
    }

    /// <summary>
    /// Returns true when the exception (or any exception in its InnerException chain, up to
    /// 5 hops) represents an authentication or credential failure. Mirrors the classifier
    /// used by BotOrchestrator (PR #181) without taking a dependency on CircuitBreakerManager.
    /// </summary>
    private static bool IsAuthError(Exception ex)
    {
        var current = ex;
        for (var hop = 0; hop < 5 && current is not null; hop++, current = current.InnerException)
        {
            var msg = current.Message;
            foreach (var (fragment, comparison) in _knownCredentialErrors)
            {
                if (msg.Contains(fragment, comparison))
                    return true;
            }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give the funding-rate fetcher time to seed the database on first run.
        try
        {
            await Task.Delay(_initialDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Use a monotonic timestamp anchor so the drift-compensation math is immune to
        // wall-clock jumps (NTP corrections, VM pause/resume, DST changes).
        var startTimestamp = Stopwatch.GetTimestamp();
        var tickIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshTiersAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Leverage tier refresh cycle aborted: {ExceptionType}", ex.GetBaseException().GetType().Name);
            }

            tickIndex++;
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var nextNominal = tickIndex * _refreshInterval;
            var delay = nextNominal - elapsed;

            if (delay < TimeSpan.Zero)
            {
                // Large overrun (e.g. sustained network partition): reset the baseline so
                // we don't hot-spin catching up — cap catch-up to one cycle.
                startTimestamp = Stopwatch.GetTimestamp();
                tickIndex = 0;
                delay = _refreshInterval;
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RefreshTiersAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var factory = scope.ServiceProvider.GetRequiredService<IExchangeConnectorFactory>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IConnectorLifecycleManager>();

        // Pre-trade tier data is needed for all active exchanges and the assets that
        // have recent funding rate snapshots (the universe SignalEngine will evaluate).
        var latestRates = await uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        var pairs = latestRates
            .Where(r => r.Exchange is not null && r.Asset?.Symbol is not null)
            .Select(r => (ExchangeName: r.Exchange!.Name, Symbol: r.Asset!.Symbol))
            .Distinct()
            .ToList();

        if (pairs.Count == 0)
        {
            _logger.LogDebug("Leverage tier refresh skipped — no active (exchange, asset) pairs found");
            return;
        }

        // Shared connectors are managed by DI — no disposal here.
        var connectorCache = new Dictionary<string, IExchangeConnector>(StringComparer.OrdinalIgnoreCase);

        // Tracks the per-exchange outcome for cycle-summary accounting.
        var outcomes = new Dictionary<string, RefreshOutcome>(StringComparer.OrdinalIgnoreCase);

        // Accumulates the first failure reason per exchange for per-cycle Warning emission.
        // ConnectorUnavailable exchanges are NOT added here — they are skipped, not failed.
        var perExchangeFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var totalExchanges = pairs
            .Select(p => p.ExchangeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        foreach (var (exchangeName, symbol) in pairs)
        {
            ct.ThrowIfCancellationRequested();

            if (!connectorCache.TryGetValue(exchangeName, out var connector))
            {
                try
                {
                    connector = factory.GetConnector(exchangeName);
                    connectorCache[exchangeName] = connector;
                }
                catch (Exception ex)
                {
                    // Connector unavailable — skip this exchange. Do NOT add to perExchangeFailures
                    // so that skipped exchanges never trigger a per-exchange Warning log.
                    _logger.LogDebug(
                        "Skipping tier refresh for {Exchange}: connector unavailable ({Error})",
                        exchangeName, ex.GetBaseException().GetType().Name);

                    outcomes.TryAdd(exchangeName, RefreshOutcome.ConnectorUnavailable);
                    continue;
                }
            }

            try
            {
                await lifecycle.EnsureTiersCachedAsync(connector, symbol, ct);
                outcomes.TryAdd(exchangeName, RefreshOutcome.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Tier refresh failed for {Exchange}/{Symbol}: {Error}",
                    exchangeName, symbol, ex.GetBaseException().GetType().Name);

                var isAuth = IsAuthError(ex);

                // Use a sanitized reason to avoid logging credential material embedded in
                // exception messages (CWE-532 log leakage, CWE-209 info exposure).
                string reason;
                if (isAuth)
                {
                    reason = "AUTH: credential rejected";
                }
                else
                {
                    reason = ex.GetBaseException().GetType().Name;
                }

                // Reclassify on failure: if this exchange was previously recorded as Success
                // (an earlier symbol succeeded) and a later symbol fails, update the outcome
                // so the summary accounting is consistent with the Warning count.
                // First failure classification wins; matches TryAdd semantics on perExchangeFailures.
                if (!outcomes.TryGetValue(exchangeName, out var existing) || existing == RefreshOutcome.Success)
                {
                    outcomes[exchangeName] = isAuth ? RefreshOutcome.CredentialFailure : RefreshOutcome.TransientFailure;
                }

                // Record the first failure reason per exchange for per-cycle Warning emission.
                perExchangeFailures.TryAdd(exchangeName, reason);
            }
        }

        // Emit one Warning per failed exchange — never per (exchange, symbol).
        // Skipped (ConnectorUnavailable) exchanges are excluded from this loop.
        foreach (var (name, reason) in perExchangeFailures)
        {
            _logger.LogWarning(
                "Leverage tier refresh failed for {Exchange}: {Reason}",
                name, reason);
        }

        // Single pass over outcomes to compute both counts simultaneously.
        int successCount = 0, skippedCount = 0;
        foreach (var o in outcomes.Values)
        {
            if (o == RefreshOutcome.Success) successCount++;
            else if (o == RefreshOutcome.ConnectorUnavailable) skippedCount++;
        }

        _logger.LogInformation(
            "Leverage tier refresh cycle completed: {Success}/{Total} succeeded, {Skipped} skipped",
            successCount, totalExchanges, skippedCount);
    }
}
