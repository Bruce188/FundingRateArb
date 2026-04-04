using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class ExchangeAnalyticsService : IExchangeAnalyticsService
{
    private readonly ICoinGlassAnalyticsRepository _analyticsRepo;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ExchangeAnalyticsService> _logger;

    public ExchangeAnalyticsService(
        ICoinGlassAnalyticsRepository analyticsRepo,
        IUnitOfWork uow,
        ILogger<ExchangeAnalyticsService> logger)
    {
        _analyticsRepo = analyticsRepo;
        _uow = uow;
        _logger = logger;
    }

    public async Task<List<ExchangeOverviewDto>> GetExchangeOverviewAsync(CancellationToken ct = default)
    {
        var latestRates = await _analyticsRepo.GetLatestSnapshotPerExchangeAsync(ct);
        return await GetExchangeOverviewAsync(latestRates, ct);
    }

    public async Task<List<ExchangeOverviewDto>> GetExchangeOverviewAsync(List<CoinGlassExchangeRate> latestRates, CancellationToken ct = default)
    {
        var exchanges = await _uow.Exchanges.GetAllAsync();
        var exchangeByName = exchanges.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

        var grouped = latestRates
            .GroupBy(r => r.SourceExchange, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var exchangeName = g.Key;
                var hasConnector = exchangeByName.TryGetValue(exchangeName, out var ex) && !ex.IsDataOnly;
                var isPlanned = exchangeByName.TryGetValue(exchangeName, out var ex2) && ex2.IsPlanned;

                string statusBadge;
                if (hasConnector)
                {
                    statusBadge = "Active Connector";
                }
                else if (isPlanned)
                {
                    statusBadge = "Planned";
                }
                else
                {
                    statusBadge = "Available";
                }

                return new ExchangeOverviewDto
                {
                    ExchangeName = exchangeName,
                    CoinCount = g.Select(r => r.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    HasDirectConnector = hasConnector,
                    IsPlanned = isPlanned,
                    StatusBadge = statusBadge,
                    LastSeen = g.Max(r => r.SnapshotTime)
                };
            })
            .OrderByDescending(e => e.HasDirectConnector)
            .ThenByDescending(e => e.IsPlanned)
            .ThenByDescending(e => e.CoinCount)
            .ToList();

        return grouped;
    }

    public async Task<List<SpreadOpportunityDto>> GetTopOpportunitiesAsync(
        int count = 20, decimal minSpreadPerHour = 0.00005m, CancellationToken ct = default)
    {
        var latestRates = await _analyticsRepo.GetLatestSnapshotPerExchangeAsync(ct);
        return await GetTopOpportunitiesAsync(latestRates, count, minSpreadPerHour, ct);
    }

    public async Task<List<SpreadOpportunityDto>> GetTopOpportunitiesAsync(
        List<CoinGlassExchangeRate> latestRates, int count = 20, decimal minSpreadPerHour = 0.00005m, CancellationToken ct = default)
    {
        var connectorExchanges = await GetConnectorExchangeNamesAsync();

        // Group by symbol: only consider coins on 2+ exchanges
        var bySymbol = latestRates
            .GroupBy(r => r.Symbol, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2);

        var opportunities = new List<SpreadOpportunityDto>();

        foreach (var symbolGroup in bySymbol)
        {
            var ratesForSymbol = symbolGroup.ToList();

            // Compute pairwise spreads
            for (int i = 0; i < ratesForSymbol.Count; i++)
            {
                for (int j = i + 1; j < ratesForSymbol.Count; j++)
                {
                    var a = ratesForSymbol[i];
                    var b = ratesForSymbol[j];

                    // Determine long and short: long pays lower rate, short pays higher rate
                    var (longExch, shortExch) = a.RatePerHour < b.RatePerHour
                        ? (a, b)
                        : (b, a);

                    var spreadPerHour = shortExch.RatePerHour - longExch.RatePerHour;
                    if (spreadPerHour < minSpreadPerHour)
                    {
                        continue;
                    }

                    var longFee = ExchangeFeeConstants.GetTakerFeeRate(longExch.SourceExchange) / 24m;
                    var shortFee = ExchangeFeeConstants.GetTakerFeeRate(shortExch.SourceExchange) / 24m;
                    var estFeesPerHour = longFee + shortFee;
                    var netYieldPerHour = spreadPerHour - estFeesPerHour;
                    var apr = netYieldPerHour * 24m * 365m * 100m;

                    var longHasConnector = connectorExchanges.Contains(longExch.SourceExchange);
                    var shortHasConnector = connectorExchanges.Contains(shortExch.SourceExchange);
                    var bothHaveConnectors = longHasConnector && shortHasConnector;
                    var oneHasConnector = longHasConnector || shortHasConnector;

                    string connectorStatus;
                    if (bothHaveConnectors)
                    {
                        connectorStatus = "Capturable";
                    }
                    else if (oneHasConnector)
                    {
                        connectorStatus = "Partial";
                    }
                    else
                    {
                        connectorStatus = "None";
                    }

                    opportunities.Add(new SpreadOpportunityDto
                    {
                        Symbol = symbolGroup.Key,
                        LongExchange = longExch.SourceExchange,
                        ShortExchange = shortExch.SourceExchange,
                        SpreadPerHour = spreadPerHour,
                        EstFeesPerHour = estFeesPerHour,
                        NetYieldPerHour = netYieldPerHour,
                        Apr = apr,
                        BothHaveConnectors = bothHaveConnectors,
                        OneHasConnector = oneHasConnector,
                        ConnectorStatus = connectorStatus
                    });
                }
            }
        }

        return opportunities
            .OrderByDescending(o => o.NetYieldPerHour)
            .Take(count)
            .ToList();
    }

    public async Task<List<RateComparisonDto>> GetRateComparisonsAsync(CancellationToken ct = default)
    {
        var connectorExchanges = await GetConnectorExchangeNamesAsync();

        // Get CoinGlass rates for exchanges that have direct connectors
        var latestCgRates = await _analyticsRepo.GetLatestSnapshotPerExchangeAsync(ct);
        var cgRatesForConnectors = latestCgRates
            .Where(r => connectorExchanges.Contains(r.SourceExchange))
            .ToList();

        if (cgRatesForConnectors.Count == 0)
        {
            return [];
        }

        // Get latest direct connector rates from FundingRateSnapshot
        var directSnapshots = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();
        var exchanges = await _uow.Exchanges.GetActiveAsync();
        var assets = await _uow.Assets.GetActiveAsync();

        var exchangeById = exchanges.ToDictionary(e => e.Id);
        var assetById = assets.ToDictionary(a => a.Id);

        // Build lookup: (exchangeName, symbol) -> directRate
        var directRateLookup = new Dictionary<(string Exchange, string Symbol), decimal>(
            new ExchangeSymbolComparer());

        foreach (var snap in directSnapshots)
        {
            if (!exchangeById.TryGetValue(snap.ExchangeId, out var exch))
            {
                continue;
            }

            if (!assetById.TryGetValue(snap.AssetId, out var asset))
            {
                continue;
            }

            directRateLookup[(exch.Name, asset.Symbol)] = snap.RatePerHour;
        }

        var comparisons = new List<RateComparisonDto>();

        foreach (var cgRate in cgRatesForConnectors)
        {
            if (!directRateLookup.TryGetValue((cgRate.SourceExchange, cgRate.Symbol), out var directRate))
            {
                continue;
            }

            var divergence = directRate != 0
                ? Math.Abs((cgRate.RatePerHour - directRate) / directRate) * 100m
                : cgRate.RatePerHour != 0 ? 100m : 0m;

            comparisons.Add(new RateComparisonDto
            {
                Symbol = cgRate.Symbol,
                ExchangeName = cgRate.SourceExchange,
                DirectRate = directRate,
                CoinGlassRate = cgRate.RatePerHour,
                DivergencePercent = Math.Round(divergence, 2),
                IsWarning = divergence > 10m
            });
        }

        return comparisons
            .OrderByDescending(c => c.DivergencePercent)
            .ToList();
    }

    public async Task<List<DiscoveryEventDto>> GetRecentDiscoveryEventsAsync(int days = 7, CancellationToken ct = default)
    {
        var events = await _analyticsRepo.GetDiscoveryEventsAsync(days, ct);

        return events.Select(e => new DiscoveryEventDto
        {
            EventType = e.EventType.ToString(),
            ExchangeName = e.ExchangeName,
            Symbol = e.Symbol,
            DiscoveredAt = e.DiscoveredAt
        }).ToList();
    }

    /// <summary>
    /// Gets exchange names that have direct connectors (IsDataOnly = false, IsActive = true).
    /// </summary>
    private async Task<HashSet<string>> GetConnectorExchangeNamesAsync()
    {
        var exchanges = await _uow.Exchanges.GetActiveAsync();
        return exchanges
            .Where(e => !e.IsDataOnly)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitive comparer for (Exchange, Symbol) tuples.
    /// </summary>
    private sealed class ExchangeSymbolComparer : IEqualityComparer<(string Exchange, string Symbol)>
    {
        public bool Equals((string Exchange, string Symbol) x, (string Exchange, string Symbol) y) =>
            string.Equals(x.Exchange, y.Exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Symbol, y.Symbol, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Exchange, string Symbol) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Exchange),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Symbol));
    }
}
