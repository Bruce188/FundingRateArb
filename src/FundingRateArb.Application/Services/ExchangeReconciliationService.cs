using System.Text.Json;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

/// <summary>
/// Periodic 5-minute reconciliation across exchanges. Five passes per run:
/// funding-rate freshness, position state, capital consistency, phantom-fee detection,
/// fee delta vs exchange-reported commission income.
/// </summary>
/// <remarks>
/// AC#4 (admin Status page section reading the most recent <see cref="ReconciliationReport"/>)
/// is DEFERRED to Feature 6 (status page). The Feature 6 implementer should query
/// <see cref="IReconciliationReportRepository.GetMostRecentAsync"/> and render the
/// per-exchange equity, anomaly counters, and degraded-exchanges list. No production
/// code path on Status page rendering exists in this iteration — the repo method is
/// the documented hand-off surface.
/// </remarks>
public class ExchangeReconciliationService : IExchangeReconciliationService
{
    // Tolerance constants (intentionally hardcoded this iteration; per-exchange overrides deferred).
    private const decimal FundingRateRatioLowerBound = 0.99m;
    private const decimal FundingRateRatioUpperBound = 1.01m;
    private static readonly TimeSpan FundingRateMaxStaleness = TimeSpan.FromMinutes(5);
    private const decimal CapitalDeltaTolerancePct = 0.01m;
    private const decimal FeeDeltaTolerancePct = 0.10m;
    private static readonly TimeSpan FeeReconcileWindow = TimeSpan.FromHours(24);

    private readonly IExchangeConnectorFactory _connectorFactory;
    private readonly IUnitOfWork _uow;
    private readonly IBalanceAggregator _balanceAggregator;
    private readonly ILogger<ExchangeReconciliationService> _logger;

    public ExchangeReconciliationService(
        IExchangeConnectorFactory connectorFactory,
        IUnitOfWork uow,
        IBalanceAggregator balanceAggregator,
        ILogger<ExchangeReconciliationService> logger)
    {
        _connectorFactory = connectorFactory;
        _uow = uow;
        _balanceAggregator = balanceAggregator;
        _logger = logger;
    }

    public async Task<ReconciliationRunResult> RunReconciliationAsync(CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var report = new ReconciliationReport { RunAtUtc = startedAt };
        var anomalies = new List<string>();
        var degradedExchanges = new List<string>();
        var perExchangeEquity = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var connectors = _connectorFactory.GetAllConnectors()
            .Where(c => c.HasCredentials)
            .ToList();

        await RunFundingRateFreshnessPassAsync(connectors, report, anomalies, degradedExchanges, ct);
        await RunPositionStatePassAsync(connectors, report, anomalies, degradedExchanges, ct);
        await RunCapitalConsistencyPassAsync(connectors, report, anomalies, degradedExchanges, perExchangeEquity, ct);
        await RunPhantomFeeDetectorAsync(report, anomalies, ct);
        await RunFeeReconciliationPassAsync(connectors, report, anomalies, degradedExchanges, startedAt, ct);

        report.PerExchangeEquityJson = JsonSerializer.Serialize(perExchangeEquity);
        report.DegradedExchangesJson = JsonSerializer.Serialize(degradedExchanges.Distinct());
        report.AnomalySummary = string.Join(" | ", anomalies).Truncate(4000);
        report.OverallStatus = anomalies.Count > 0 ? "Unhealthy"
            : degradedExchanges.Count > 0 ? "Degraded"
            : "Healthy";
        report.DurationMs = (int)Math.Min(int.MaxValue, (DateTime.UtcNow - startedAt).TotalMilliseconds);
        return new ReconciliationRunResult(report, anomalies);
    }

    private async Task RunFundingRateFreshnessPassAsync(
        List<IExchangeConnector> connectors,
        ReconciliationReport report,
        List<string> anomalies,
        List<string> degradedExchanges,
        CancellationToken ct)
    {
        // Get DB snapshots (latest per exchange/asset) for staleness and ratio comparison.
        var dbSnapshots = await _uow.FundingRates.GetLatestPerExchangePerAssetAsync();

        // Get open positions to determine which (asset, exchange) pairs are active.
        var openPositions = await _uow.Positions.GetByStatusAsync(PositionStatus.Open);

        foreach (var connector in connectors)
        {
            try
            {
                var liveRates = await connector.GetFundingRatesAsync(ct);
                var liveRateMap = liveRates.ToDictionary(
                    r => r.Symbol,
                    r => r,
                    StringComparer.OrdinalIgnoreCase);

                // Determine which assets are active on this exchange.
                var exchangeAssets = openPositions
                    .Where(p =>
                        (p.LongExchange?.Name != null && string.Equals(p.LongExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)) ||
                        (p.ShortExchange?.Name != null && string.Equals(p.ShortExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Asset?.Symbol)
                    .Where(s => s != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var now = DateTime.UtcNow;

                foreach (var assetSymbol in exchangeAssets)
                {
                    if (assetSymbol == null) continue;

                    // Find the most recent DB snapshot for this (asset, exchange) pair.
                    var dbSnapshot = dbSnapshots
                        .Where(s =>
                            string.Equals(s.Exchange?.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.Asset?.Symbol, assetSymbol, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(s => s.RecordedAt)
                        .FirstOrDefault();

                    if (dbSnapshot == null || (now - dbSnapshot.RecordedAt) > FundingRateMaxStaleness)
                    {
                        var age = dbSnapshot == null ? "N/A" : $"{(now - dbSnapshot.RecordedAt).TotalMinutes:F1}";
                        report.FreshRateMismatchCount++;
                        anomalies.Add($"{connector.ExchangeName}/{assetSymbol}: DB rate stale ({age}m)");
                        continue;
                    }

                    // Compare DB rate vs live exchange rate (using RatePerHour as the comparable unit).
                    if (liveRateMap.TryGetValue(assetSymbol, out var liveRate) && liveRate.RatePerHour != 0)
                    {
                        var ratio = dbSnapshot.RatePerHour / liveRate.RatePerHour;
                        if (ratio < FundingRateRatioLowerBound || ratio > FundingRateRatioUpperBound)
                        {
                            report.FreshRateMismatchCount++;
                            anomalies.Add($"{connector.ExchangeName}/{assetSymbol}: rate ratio {ratio:F4} out of [{FundingRateRatioLowerBound}, {FundingRateRatioUpperBound}]");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Funding rate freshness pass failed for {Exchange}", connector.ExchangeName);
                degradedExchanges.Add(connector.ExchangeName);
            }
        }
    }

    private async Task RunPositionStatePassAsync(
        List<IExchangeConnector> connectors,
        ReconciliationReport report,
        List<string> anomalies,
        List<string> degradedExchanges,
        CancellationToken ct)
    {
        var openPositions = await _uow.Positions.GetByStatusAsync(PositionStatus.Open);

        foreach (var connector in connectors)
        {
            try
            {
                var exchangePositions = await connector.GetAllOpenPositionsAsync(ct);
                if (exchangePositions == null) continue; // unsupported — not an error

                // Build a set of (asset, side) tuples present in the DB for this exchange.
                var dbPairs = openPositions
                    .Where(p =>
                        (p.LongExchange?.Name != null && string.Equals(p.LongExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)) ||
                        (p.ShortExchange?.Name != null && string.Equals(p.ShortExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)))
                    .SelectMany(p =>
                    {
                        var pairs = new List<(string Asset, Side Side)>();
                        if (p.LongExchange?.Name != null && string.Equals(p.LongExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase) && p.Asset != null)
                            pairs.Add((p.Asset.Symbol, Side.Long));
                        if (p.ShortExchange?.Name != null && string.Equals(p.ShortExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase) && p.Asset != null)
                            pairs.Add((p.Asset.Symbol, Side.Short));
                        return pairs;
                    })
                    .ToHashSet();

                foreach (var (asset, side, _) in exchangePositions)
                {
                    if (!dbPairs.Contains((asset, side)))
                    {
                        report.OrphanPositionCount++;
                        anomalies.Add($"Orphan position: {asset}/{side} on {connector.ExchangeName} not in DB");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Position state pass failed for {Exchange}", connector.ExchangeName);
                degradedExchanges.Add(connector.ExchangeName);
            }
        }
    }

    private async Task RunCapitalConsistencyPassAsync(
        List<IExchangeConnector> connectors,
        ReconciliationReport report,
        List<string> anomalies,
        List<string> degradedExchanges,
        Dictionary<string, decimal> perExchangeEquity,
        CancellationToken ct)
    {
        try
        {
            var enabledUserIds = await _uow.UserConfigurations.GetAllEnabledUserIdsAsync();
            foreach (var userId in enabledUserIds)
            {
                try
                {
                    var snapshot = await _balanceAggregator.GetBalanceSnapshotAsync(userId, ct);
                    foreach (var balance in snapshot.Balances)
                    {
                        if (!balance.IsUnavailable)
                        {
                            perExchangeEquity.TryGetValue(balance.ExchangeName, out var current);
                            perExchangeEquity[balance.ExchangeName] = current + balance.AvailableUsdc;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Capital consistency pass: failed to fetch balance for user {UserId}", userId);
                }
            }

            // Compare per-exchange equity vs sum of open position notional (SizeUsdc / Leverage).
            var openPositions = await _uow.Positions.GetByStatusAsync(PositionStatus.Open);
            foreach (var connector in connectors)
            {
                if (!perExchangeEquity.TryGetValue(connector.ExchangeName, out var equity)) continue;
                if (equity <= 0) continue;

                var dbNotional = openPositions
                    .Where(p =>
                        (p.LongExchange?.Name != null && string.Equals(p.LongExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)) ||
                        (p.ShortExchange?.Name != null && string.Equals(p.ShortExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)))
                    .Sum(p => p.Leverage > 0 ? p.SizeUsdc / p.Leverage : 0);

                var denominator = Math.Max(equity, dbNotional);
                if (denominator > 0)
                {
                    var delta = Math.Abs(equity - dbNotional) / denominator;
                    if (delta > CapitalDeltaTolerancePct)
                    {
                        anomalies.Add($"{connector.ExchangeName}: capital delta {delta:P2} outside {CapitalDeltaTolerancePct:P0} tolerance (equity {equity:F2}, db notional {dbNotional:F2})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capital consistency pass failed");
            foreach (var connector in connectors)
                degradedExchanges.Add(connector.ExchangeName);
        }
    }

    private async Task RunPhantomFeeDetectorAsync(
        ReconciliationReport report,
        List<string> anomalies,
        CancellationToken ct)
    {
        var count = await _uow.Positions.CountPhantomFeeRowsSinceAsync(DateTime.UtcNow.AddHours(-24), ct);
        report.PhantomFeeRowCount24h = count;
        if (count > 0)
        {
            anomalies.Add($"Phantom-fee rows detected (last 24h): {count} stuck Failed positions with non-zero fees");
        }
    }

    private async Task RunFeeReconciliationPassAsync(
        List<IExchangeConnector> connectors,
        ReconciliationReport report,
        List<string> anomalies,
        List<string> degradedExchanges,
        DateTime startedAt,
        CancellationToken ct)
    {
        var windowStart = startedAt - FeeReconcileWindow;
        var closedPositions = await _uow.Positions.GetClosedSinceAsync(windowStart);

        foreach (var connector in connectors)
        {
            try
            {
                // Sum DB-recorded fees for positions closed on this exchange.
                var dbFees = closedPositions
                    .Where(p =>
                        (p.LongExchange?.Name != null && string.Equals(p.LongExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)) ||
                        (p.ShortExchange?.Name != null && string.Equals(p.ShortExchange.Name, connector.ExchangeName, StringComparison.OrdinalIgnoreCase)))
                    .Sum(p => p.EntryFeesUsdc + p.ExitFeesUsdc);

                var commission = await connector.GetCommissionIncomeAsync(windowStart, startedAt, ct);
                if (commission == null)
                {
                    // Exchange does not expose commission income — not a degraded state.
                    _logger.LogInformation("Fee reconciliation: {Exchange} does not support GetCommissionIncomeAsync — skipping", connector.ExchangeName);
                    continue;
                }

                if (commission == 0 && dbFees == 0) continue; // both zero — no delta

                var denominator = commission.Value != 0 ? Math.Abs(commission.Value) : Math.Abs(dbFees);
                if (denominator > 0)
                {
                    var delta = Math.Abs(dbFees - commission.Value) / denominator;
                    if (delta > FeeDeltaTolerancePct)
                    {
                        report.FeeDeltaOutsideToleranceCount++;
                        anomalies.Add($"{connector.ExchangeName}: fee delta {delta:P2} outside {FeeDeltaTolerancePct:P0} tolerance (db {dbFees:F4}, exchange {commission.Value:F4})");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fee reconciliation pass failed for {Exchange}", connector.ExchangeName);
                degradedExchanges.Add(connector.ExchangeName);
            }
        }
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
