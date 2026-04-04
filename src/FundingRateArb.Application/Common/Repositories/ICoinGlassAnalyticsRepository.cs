using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Application.Common.Repositories;

public interface ICoinGlassAnalyticsRepository
{
    Task SaveSnapshotAsync(List<CoinGlassExchangeRate> rates, CancellationToken ct = default);
    Task<List<CoinGlassExchangeRate>> GetLatestSnapshotPerExchangeAsync(CancellationToken ct = default);
    Task<List<CoinGlassExchangeRate>> GetSnapshotsAsync(DateTime from, DateTime to, int maxRows = 10_000, CancellationToken ct = default);
    Task<List<CoinGlassDiscoveryEvent>> GetDiscoveryEventsAsync(int days = 7, CancellationToken ct = default);
    Task SaveDiscoveryEventsAsync(List<CoinGlassDiscoveryEvent> events, CancellationToken ct = default);
    Task PruneOldSnapshotsAsync(int retentionDays = 7, CancellationToken ct = default);
    Task<HashSet<(string Exchange, string Symbol)>> GetKnownPairsAsync(CancellationToken ct = default);
}
