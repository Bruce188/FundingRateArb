namespace FundingRateArb.Application.Interfaces;

/// <summary>
/// Read-only snapshot of the currently-denied <c>(LongExchangeName, ShortExchangeName)</c> tuples.
/// Consumed by <c>SignalEngine.ComputeAndCacheAsync</c> on the hot path. Implementations MUST be
/// thread-safe for read; writes happen only via <see cref="IPairDenyListProvider.RefreshAsync"/>
/// which atomically swaps the <see cref="IPairDenyListProvider.Current"/> reference.
/// </summary>
public interface IPairDenyListSnapshot
{
    /// <summary>True when <paramref name="longExchangeName"/>/<paramref name="shortExchangeName"/> is currently denied.
    /// Match is case-insensitive (<c>StringComparer.OrdinalIgnoreCase</c>). The "denied" condition is
    /// <c>IsDenied = true AND (DeniedUntil IS NULL OR DeniedUntil &gt; NOW)</c> — the strict inequality
    /// on <c>DeniedUntil</c> means the moment it equals <c>NOW</c>, the pair is allowed.</summary>
    bool IsDenied(string longExchangeName, string shortExchangeName);

    /// <summary>Number of denied tuples in this snapshot — used by the admin UI summary card.</summary>
    int Count { get; }

    /// <summary>UTC timestamp at which this snapshot was built. Diagnostic only.</summary>
    DateTime SnapshotAt { get; }
}

/// <summary>
/// Singleton provider for the currently-active <see cref="IPairDenyListSnapshot"/>. <c>BotOrchestrator</c>
/// calls <see cref="RefreshAsync"/> once per cycle; admin POST handlers also call it to make manual
/// changes take effect immediately on the next read.
/// </summary>
public interface IPairDenyListProvider
{
    /// <summary>Atomic reference to the current snapshot. Reading <c>Current</c> is thread-safe and
    /// non-blocking — implementations swap a new <see cref="IPairDenyListSnapshot"/> reference inside
    /// <see cref="RefreshAsync"/>; readers see either the old or new snapshot, never a torn read.</summary>
    IPairDenyListSnapshot Current { get; }

    /// <summary>Rebuilds the snapshot from <c>IPairExecutionStatsRepository.GetCurrentlyDeniedKeysAsync</c>
    /// and atomically swaps it onto <see cref="Current"/>. Safe to call concurrently with reads.</summary>
    Task RefreshAsync(CancellationToken ct);
}
