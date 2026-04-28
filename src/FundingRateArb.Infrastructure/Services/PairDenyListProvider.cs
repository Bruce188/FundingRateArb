using System.Collections.Immutable;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace FundingRateArb.Infrastructure.Services;

public class PairDenyListProvider : IPairDenyListProvider
{
    private static readonly IEqualityComparer<(string, string)> CaseInsensitiveTupleComparer =
        new TupleOrdinalIgnoreCaseComparer();

    private readonly IServiceScopeFactory _scopeFactory;
    private volatile IPairDenyListSnapshot _current;

    public PairDenyListProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _current = new Snapshot(ImmutableHashSet<(string, string)>.Empty.WithComparer(CaseInsensitiveTupleComparer), DateTime.UtcNow);
    }

    public IPairDenyListSnapshot Current => _current;

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var keys = await uow.PairExecutionStats.GetCurrentlyDeniedKeysAsync(ct);
        var set = ImmutableHashSet.CreateRange(CaseInsensitiveTupleComparer, keys);
        _current = new Snapshot(set, DateTime.UtcNow);
    }

    private sealed class Snapshot : IPairDenyListSnapshot
    {
        private readonly ImmutableHashSet<(string, string)> _set;

        public Snapshot(ImmutableHashSet<(string, string)> set, DateTime snapshotAt)
        {
            _set = set;
            SnapshotAt = snapshotAt;
        }

        public bool IsDenied(string longExchangeName, string shortExchangeName)
        {
            if (string.IsNullOrEmpty(longExchangeName) || string.IsNullOrEmpty(shortExchangeName))
            {
                return false;
            }

            return _set.Contains((longExchangeName, shortExchangeName));
        }

        public int Count => _set.Count;
        public DateTime SnapshotAt { get; }
    }

    private sealed class TupleOrdinalIgnoreCaseComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1 ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2 ?? string.Empty));
    }
}
