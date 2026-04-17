using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that pre-fetches leverage tier data for all active (exchange, asset)
/// pairs on startup and refreshes hourly. Prevents the first trade opportunity from opening
/// against cold cache state where the effective leverage cap would fall back to user config
/// without bracket awareness.
/// </summary>
public class LeverageTierRefresher : BackgroundService
{
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(1);

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
        _scopeFactory = scopeFactory;
        _logger = logger;
        _initialDelay = initialDelay;
        _refreshInterval = refreshInterval;
    }

    /// <summary>
    /// Returns true when the exception represents an authentication or credential failure.
    /// Mirrors the classifier used by BotOrchestrator (PR #181) without taking a dependency
    /// on CircuitBreakerManager internals.
    /// </summary>
    private static bool IsAuthError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("Invalid API-key", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("-2015", StringComparison.Ordinal)
            || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("credentials not provided", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give funding rate fetcher time to seed the database on first run
        try
        {
            await Task.Delay(_initialDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

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
                _logger.LogWarning(ex, "Leverage tier refresh cycle aborted: {Reason}", ex.Message);
            }

            try
            {
                await Task.Delay(_refreshInterval, ct);
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

        // Accumulates the first failure reason per exchange for per-cycle Warning emission.
        var perExchangeFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Track all exchanges seen so we can compute SuccessCount / TotalCount.
        var allExchanges = pairs
            .Select(p => p.ExchangeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var totalExchanges = allExchanges.Count;

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
                    _logger.LogDebug(
                        "Skipping tier refresh for {Exchange}: connector unavailable ({Error})",
                        exchangeName, ex.Message);
                    continue;
                }
            }

            try
            {
                await lifecycle.EnsureTiersCachedAsync(connector, symbol, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Tier refresh failed for {Exchange}/{Symbol}: {Error}",
                    exchangeName, symbol, ex.Message);

                var reason = ex.GetType().Name + ": " + ex.Message;
                if (IsAuthError(ex))
                {
                    reason = "AUTH: " + reason;
                }

                // TryAdd ensures only the first failure per exchange is recorded per cycle.
                perExchangeFailures.TryAdd(exchangeName, reason);
            }
        }

        // Emit one Warning per failed exchange — never per (exchange, symbol).
        foreach (var (name, reason) in perExchangeFailures)
        {
            _logger.LogWarning(
                "Leverage tier refresh failed for {Exchange}: {Reason}",
                name, reason);
        }

        var successCount = totalExchanges - perExchangeFailures.Count;

        _logger.LogInformation(
            "Leverage tier refresh cycle completed with {SuccessCount}/{TotalCount} exchanges",
            successCount, totalExchanges);
    }
}
