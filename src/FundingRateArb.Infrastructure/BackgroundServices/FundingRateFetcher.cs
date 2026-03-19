using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class FundingRateFetcher : BackgroundService
{
    // M-FR3: Named constant instead of magic number
    private const int FetchIntervalSeconds = 60;

    // H-FR1: Track last purge time to run hourly purge without a separate timer
    private DateTime _lastPurgeUtc = DateTime.MinValue;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hubContext;
    private readonly ILogger<FundingRateFetcher> _logger;

    public FundingRateFetcher(
        IServiceScopeFactory scopeFactory,
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        ILogger<FundingRateFetcher> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext   = hubContext;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // M-FR3: Run once immediately so the dashboard has data on startup (no 60s blank wait)
        try
        {
            await FetchAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial funding rate fetch failed");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(FetchIntervalSeconds));

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await FetchAllAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Funding rate fetch cycle failed");
            }
        }
    }

    /// <summary>
    /// Fetches rates from all active exchanges and persists them as FundingRateSnapshot rows.
    /// Exposed as internal for unit testing.
    /// </summary>
    internal async Task FetchAllAsync(CancellationToken ct)
    {
        using var scope  = _scopeFactory.CreateScope();
        var uow          = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var factory      = scope.ServiceProvider.GetRequiredService<IExchangeConnectorFactory>();

        var connectors = factory.GetAllConnectors().ToList();

        // M8: Fetch from all exchanges in parallel instead of sequentially
        var tasks = connectors.Select(async c =>
        {
            try
            {
                var rates = await c.GetFundingRatesAsync(ct);
                _logger.LogDebug("Fetched {Count} rates from {Exchange}", rates.Count, c.ExchangeName);
                return rates;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch funding rates from {Exchange}", c.ExchangeName);
                return new List<FundingRateDto>();
            }
        });
        var results  = await Task.WhenAll(tasks);
        var allRates = results.SelectMany(r => r).ToList();

        // Resolve exchange and asset lookup tables once
        var exchanges = await uow.Exchanges.GetActiveAsync();
        var assets    = await uow.Assets.GetActiveAsync();

        // H-FR2: Build lookup dictionaries before the loop — O(N+M) instead of O(N*M)
        var exchangeMap = exchanges.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
        var assetMap    = assets.ToDictionary(a => a.Symbol, StringComparer.OrdinalIgnoreCase);

        // M9: Build snapshot list first, then AddRange once (instead of individual Add calls)
        var now = DateTime.UtcNow;
        var snapshots = new List<FundingRateSnapshot>();

        foreach (var rate in allRates)
        {
            if (!exchangeMap.TryGetValue(rate.ExchangeName, out var exchange)) continue;
            if (!assetMap.TryGetValue(rate.Symbol, out var asset)) continue;

            snapshots.Add(new FundingRateSnapshot
            {
                ExchangeId   = exchange.Id,
                AssetId      = asset.Id,
                RatePerHour  = rate.RatePerHour,
                RawRate      = rate.RawRate,
                MarkPrice    = rate.MarkPrice,
                IndexPrice   = rate.IndexPrice,
                Volume24hUsd = rate.Volume24hUsd,
                RecordedAt   = now,
            });
        }

        uow.FundingRates.AddRange(snapshots);
        await uow.SaveAsync(ct);

        _logger.LogInformation(
            "Funding rates saved: {Total} snapshots from {ExchangeCount} exchanges",
            allRates.Count, connectors.Count);

        // H-FR1: Hourly purge — keep only 48h of data to prevent unbounded table growth
        if (DateTime.UtcNow - _lastPurgeUtc >= TimeSpan.FromHours(1))
        {
            var cutoff = DateTime.UtcNow.AddHours(-48);
            var purged = await uow.FundingRates.PurgeOlderThanAsync(cutoff, ct);
            _lastPurgeUtc = DateTime.UtcNow;
            _logger.LogInformation("Purged {Count} funding rate snapshots older than {Cutoff:u}", purged, cutoff);
        }

        // M13: Push live updates consistently to Group("MarketData"), not Clients.All
        await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveFundingRateUpdate(allRates);

        // Push computed opportunities so Opportunities page updates in real-time (C4: use Group, not All)
        try
        {
            var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
            var opportunities = await signalEngine.GetOpportunitiesAsync();

            _logger.LogInformation("Pushing {Count} opportunities via SignalR", opportunities.Count);
            await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveOpportunityUpdate(opportunities);
            _logger.LogInformation("SignalR opportunity push completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push opportunity update via SignalR");
        }
    }
}
