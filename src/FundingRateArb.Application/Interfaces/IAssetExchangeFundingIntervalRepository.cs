namespace FundingRateArb.Application.Interfaces;

public interface IAssetExchangeFundingIntervalRepository
{
    Task UpsertManyAsync(
        IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SourceSnapshotId)> entries,
        CancellationToken ct);

    Task<IReadOnlyDictionary<(int ExchangeId, int AssetId), int>> GetIntervalsAsync(CancellationToken ct);

    void InvalidateCache();
}
