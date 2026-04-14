namespace FundingRateArb.Application.Common.Services;

/// <summary>
/// Runtime discovery of new tradeable assets. Invoked by the fetcher every
/// cycle with the set of symbols it just observed from the exchanges —
/// unknown symbols are created as <c>Asset</c> rows, user preferences are
/// reconciled, and the asset cache is invalidated so the caller's next
/// repository read picks up the new rows.
///
/// Concurrency: implementations must be safe for a single-instance deployment
/// where overlapping fetcher ticks (e.g. startup + scheduled timer) may
/// briefly race. Unique-constraint violations on <c>Asset.Symbol</c> are
/// treated as benign — the other writer inserted the row, so there is
/// nothing left to do.
///
/// Scope: implementations are scoped (they share the ambient
/// <c>AppDbContext</c>). Callers must resolve from a per-request/per-cycle
/// scope; a captured instance across scopes is unsafe.
/// </summary>
public interface IAssetDiscoveryService
{
    /// <summary>
    /// Ensures <c>Asset</c> rows exist for every valid symbol in
    /// <paramref name="symbols"/>. For newly-created assets, reconciles
    /// <c>UserAssetPreferences</c> so every user has an <c>IsEnabled=true</c>
    /// row, then invalidates the asset cache. Returns the count of
    /// newly-created assets (zero when nothing changed — hot path is silent).
    ///
    /// Input validation: null, empty and whitespace strings are dropped.
    /// Symbols longer than the DB column limit (currently 20 chars) are
    /// dropped with a warning — they cannot be persisted. Duplicate casings
    /// are coalesced (case-insensitive) preserving the first-seen casing.
    /// </summary>
    /// <param name="symbols">Observed symbols from the exchanges. May contain
    /// duplicates, mixed casing, or whitespace — all are normalized.</param>
    /// <param name="ct">Cancellation token forwarded to DB operations.</param>
    Task<int> EnsureAssetsExistAsync(
        IEnumerable<string> symbols, CancellationToken ct = default);
}
