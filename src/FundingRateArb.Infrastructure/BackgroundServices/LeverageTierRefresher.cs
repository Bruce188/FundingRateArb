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
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeverageTierRefresher> _logger;

    public LeverageTierRefresher(
        IServiceScopeFactory scopeFactory,
        ILogger<LeverageTierRefresher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Give funding rate fetcher time to seed the database on first run
        try
        {
            await Task.Delay(InitialDelay, ct);
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
                _logger.LogWarning(ex, "Leverage tier refresh cycle failed — retrying at next interval");
            }

            try
            {
                await Task.Delay(RefreshInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshTiersAsync(CancellationToken ct)
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
        var successCount = 0;

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
                successCount++;
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
            }
        }

        _logger.LogInformation(
            "Leverage tier refresh complete: {Success}/{Total} pairs cached",
            successCount, pairs.Count);
    }
}
