using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Services;

public class PnlReconciliationService : IPnlReconciliationService
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<PnlReconciliationService> _logger;

    /// <summary>
    /// Minimum absolute exchange PnL required before computing percentage divergence.
    /// Prevents false-positive alerts on near-zero PnL values.
    /// </summary>
    private const decimal MinPnlForDivergence = 0.01m;

    public PnlReconciliationService(IUnitOfWork uow, ILogger<PnlReconciliationService> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task ReconcileAsync(
        ArbitragePosition position,
        string assetSymbol,
        IExchangeConnector longConnector,
        IExchangeConnector shortConnector,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(assetSymbol))
        {
            _logger.LogWarning("Cannot reconcile position #{PositionId} — asset symbol not provided", position.Id);
            return;
        }

        var from = position.OpenedAt;
        var to = position.ClosedAt ?? DateTime.UtcNow;

        // Fetch realized PnL and funding from both legs in parallel
        var longPnlTask = longConnector.GetRealizedPnlAsync(assetSymbol, Side.Long, from, to, ct);
        var shortPnlTask = shortConnector.GetRealizedPnlAsync(assetSymbol, Side.Short, from, to, ct);
        var longFundingTask = longConnector.GetFundingPaymentsAsync(assetSymbol, Side.Long, from, to, ct);
        var shortFundingTask = shortConnector.GetFundingPaymentsAsync(assetSymbol, Side.Short, from, to, ct);

        await Task.WhenAll(longPnlTask, shortPnlTask, longFundingTask, shortFundingTask);

        var longPnl = await longPnlTask;
        var shortPnl = await shortPnlTask;
        var longFunding = await longFundingTask;
        var shortFunding = await shortFundingTask;

        // Sum available PnL from both legs
        decimal? exchangePnl = null;
        if (longPnl.HasValue || shortPnl.HasValue)
        {
            exchangePnl = (longPnl ?? 0m) + (shortPnl ?? 0m);
        }

        // Sum available funding from both legs
        decimal? exchangeFunding = null;
        if (longFunding.HasValue || shortFunding.HasValue)
        {
            exchangeFunding = (longFunding ?? 0m) + (shortFunding ?? 0m);
        }

        // Store exchange-reported PnL and compute divergence
        if (exchangePnl.HasValue)
        {
            position.ExchangeReportedPnl = exchangePnl.Value;

            if (position.RealizedPnl.HasValue && Math.Abs(exchangePnl.Value) > MinPnlForDivergence)
            {
                var divergence = (position.RealizedPnl.Value - exchangePnl.Value)
                    / Math.Abs(exchangePnl.Value) * 100m;
                position.PnlDivergence = divergence;

                // Create alerts based on divergence thresholds
                if (Math.Abs(divergence) > 10m)
                {
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.PnlDivergence,
                        Severity = AlertSeverity.Critical,
                        Message = $"PnL divergence {divergence:F2}% on position #{position.Id} ({assetSymbol}): " +
                            $"local={position.RealizedPnl:F4}, exchange={exchangePnl:F4}",
                    });
                }
                else if (Math.Abs(divergence) > 5m)
                {
                    _uow.Alerts.Add(new Alert
                    {
                        UserId = position.UserId,
                        ArbitragePositionId = position.Id,
                        Type = AlertType.PnlDivergence,
                        Severity = AlertSeverity.Warning,
                        Message = $"PnL divergence {divergence:F2}% on position #{position.Id} ({assetSymbol}): " +
                            $"local={position.RealizedPnl:F4}, exchange={exchangePnl:F4}",
                    });
                }
            }
        }

        // Store exchange-reported funding
        if (exchangeFunding.HasValue)
        {
            position.ExchangeReportedFunding = exchangeFunding.Value;
        }

        position.ReconciledAt = DateTime.UtcNow;

        _logger.LogDebug(
            "Reconciled position #{PositionId} ({Asset}): localPnl={LocalPnl:F4}, exchangePnl={ExchangePnl}, " +
            "divergence={Divergence}%, exchangeFunding={ExchangeFunding}",
            position.Id, assetSymbol, position.RealizedPnl,
            exchangePnl?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A",
            position.PnlDivergence?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A",
            exchangeFunding?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "N/A");
    }
}
