using System.ComponentModel.DataAnnotations;

namespace FundingRateArb.Domain.Entities;

/// <summary>
/// Per-run snapshot from <c>ExchangeReconciliationHostedService</c>. One row per tick (default 5 min).
/// Anomalies are dedup'd via 4h windowed <see cref="Alert"/> lookups; the row itself is the immutable
/// audit trail of what each pass observed.
/// </summary>
public class ReconciliationReport
{
    public int Id { get; set; }

    /// <summary>UTC start time of the reconciliation tick.</summary>
    public DateTime RunAtUtc { get; set; }

    /// <summary>End-to-end wall time of the reconciliation tick in milliseconds. Used for SLO tracking.</summary>
    public int DurationMs { get; set; }

    /// <summary>Healthy | Degraded | Unhealthy. Healthy = all 5 passes succeeded with no anomalies.
    /// Degraded = at least one exchange returned a transient error or unsupported response.
    /// Unhealthy = at least one anomaly counter is non-zero.</summary>
    [Required, MaxLength(32)]
    public string OverallStatus { get; set; } = "Healthy";

    /// <summary>JSON-serialized dictionary of per-exchange equity (USDC), e.g. {"Hyperliquid": 1234.56, ...}.</summary>
    [Required]
    public string PerExchangeEquityJson { get; set; } = string.Empty;

    /// <summary>Count of (asset, exchange) pairs where the most-recent FundingRateSnapshot is stale (>5 min)
    /// or its rate ratio vs the live exchange rate falls outside [0.99, 1.01].</summary>
    public int FreshRateMismatchCount { get; set; }

    /// <summary>Count of (asset, side, exchange) tuples observed open on the exchange API but not present
    /// in the DB as Status=Open. Detected via <see cref="FundingRateArb.Application.Common.Exchanges.IExchangeConnector.GetAllOpenPositionsAsync"/>.</summary>
    public int OrphanPositionCount { get; set; }

    /// <summary>Count of phantom-fee rows from the last 24h. Should be 0 post fix/phantom-fees-on-neither-leg-filled.</summary>
    public int PhantomFeeRowCount24h { get; set; }

    /// <summary>Count of exchanges where the DB-summed entry+exit fees diverge from the exchange-reported
    /// commission income by more than 10% over the last 24h.</summary>
    public int FeeDeltaOutsideToleranceCount { get; set; }

    /// <summary>JSON-serialized array of exchange names that returned errors during this run.</summary>
    [Required]
    public string DegradedExchangesJson { get; set; } = string.Empty;

    /// <summary>Free-form description of detected anomalies for operator readability. Capped at 4000 chars.</summary>
    [MaxLength(4000)]
    public string? AnomalySummary { get; set; }
}
