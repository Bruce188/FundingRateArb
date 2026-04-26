namespace FundingRateArb.Application.Common.Repositories;

/// <summary>
/// Repository for per-symbol, per-exchange funding interval overrides.
/// Stores the detected funding interval for each (exchange, asset) pair so that
/// opportunity calculations can use the correct interval even when the exchange-level
/// default differs from a symbol's actual settlement cadence.
/// </summary>
public interface IAssetExchangeFundingIntervalRepository
{
    /// <summary>
    /// Upserts multiple per-symbol funding interval entries in a single batch.
    /// </summary>
    /// <param name="entries">
    /// Sequence of (ExchangeId, AssetId, IntervalHours, SnapshotId) tuples.
    /// SnapshotId is the FundingRateSnapshot that triggered the detection; may be null.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertManyAsync(
        IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)> entries,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidates any in-process cache so subsequent readers see persisted data.
    /// </summary>
    void InvalidateCache();
}
