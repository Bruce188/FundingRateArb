using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Services;

public interface IExchangeReconciliationService
{
    /// <summary>
    /// Runs all five reconciliation passes and returns a populated <see cref="ReconciliationReport"/>.
    /// Anomaly descriptions are returned via the <see cref="ReconciliationRunResult.AnomalyDescriptions"/>
    /// list so the caller can dedup-and-alert; the report itself is the immutable audit row.
    /// Per-exchange API errors degrade gracefully — the failing exchange is recorded in
    /// <see cref="ReconciliationReport.DegradedExchangesJson"/>; other exchanges proceed normally.
    /// </summary>
    Task<ReconciliationRunResult> RunReconciliationAsync(CancellationToken ct = default);
}

/// <summary>Output of <see cref="IExchangeReconciliationService.RunReconciliationAsync"/>.
/// Bundles the persistable report with the anomaly descriptions ready for alert dedup.</summary>
public record ReconciliationRunResult(
    ReconciliationReport Report,
    IReadOnlyList<string> AnomalyDescriptions);
