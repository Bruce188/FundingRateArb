using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Creates <see cref="Asset"/> rows for unknown symbols discovered by the
/// fetcher and reconciles <see cref="UserAssetPreference"/> rows for the
/// newly-inserted assets.
///
/// Concurrency contract: callers share the ambient <see cref="AppDbContext"/>
/// scope via DI (the service itself and <see cref="IAssetRepository"/> both
/// resolve from the same scope), so writes done here are visible to reads
/// issued through the repository on the same <see cref="AppDbContext"/>.
/// The service is scoped — do not cache instances across scopes.
///
/// Concurrent-writer safety: when two writers race between the existence
/// check and the insert, <see cref="DbUpdateException"/> from the unique-
/// symbol index is treated as a benign no-op (another writer won). See
/// <see cref="EnsureAssetsExistAsync"/> for details.
/// </summary>
public class AssetDiscoveryService : IAssetDiscoveryService
{
    /// <summary>Hard cap on new rows inserted per cycle (NB2: protects
    /// against hostile exchange responses returning 100k symbols).</summary>
    internal const int MaxNewAssetsPerCycle = 1000;

    /// <summary>Maximum length of a symbol passed through to the DB. Mirrors
    /// <c>[MaxLength(20)]</c> on <see cref="Asset.Symbol"/> (B1).</summary>
    internal const int MaxSymbolLength = 20;

    /// <summary>Maximum number of symbols to echo verbatim in the discovery
    /// log line. Prevents log amplification on large batches (NB2).</summary>
    private const int MaxLoggedSymbols = 20;

    private readonly IAssetRepository _assetRepository;
    private readonly AppDbContext _context;
    private readonly ILogger<AssetDiscoveryService> _logger;

    public AssetDiscoveryService(
        IAssetRepository assetRepository,
        AppDbContext context,
        ILogger<AssetDiscoveryService> logger)
    {
        _assetRepository = assetRepository;
        _context = context;
        _logger = logger;
    }

    public async Task<int> EnsureAssetsExistAsync(
        IEnumerable<string> symbols, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // NB12: load existing symbols up front so the hot path (no new assets)
        // never materializes an intermediate list. We stream the input through
        // filter+trim+dedupe, testing against the existing set as we go.
        var existing = await _assetRepository.GetActiveAsync();
        var existingSymbols = new HashSet<string>(
            existing.Select(a => a.Symbol),
            StringComparer.OrdinalIgnoreCase);

        // Dedupe case-insensitively while preserving the first-seen casing (N3).
        var seenNewSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? missing = null;
        var droppedInvalid = 0;
        var truncated = 0;

        foreach (var raw in symbols)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();

            // B1: reject symbols that would violate [MaxLength(20)]. Dropping
            // the bad symbol keeps the rest of the batch intact instead of
            // aborting the whole fetch cycle.
            if (trimmed.Length is <= 0 or > MaxSymbolLength)
            {
                droppedInvalid++;
                continue;
            }

            if (existingSymbols.Contains(trimmed) || !seenNewSymbols.Add(trimmed))
            {
                continue;
            }

            if (seenNewSymbols.Count > MaxNewAssetsPerCycle)
            {
                // NB2: pop the last-added and stop. Record how many symbols were
                // dropped so the warning can quantify the skew.
                seenNewSymbols.Remove(trimmed);
                truncated++;
                // Count remaining offenders so the warning is accurate. We keep
                // iterating (rather than breaking) so the dropped/truncated
                // counts reflect the true input size.
                continue;
            }

            missing ??= new List<string>(capacity: 16);
            missing.Add(trimmed);
        }

        if (droppedInvalid > 0)
        {
            _logger.LogWarning(
                "AssetDiscovery dropped {Count} invalid symbols (null/empty/whitespace or longer than {MaxLength} chars)",
                droppedInvalid, MaxSymbolLength);
        }

        if (truncated > 0)
        {
            _logger.LogWarning(
                "AssetDiscovery truncated batch: kept {Kept} new symbols, skipped {Skipped} more beyond the per-cycle cap of {Cap}",
                seenNewSymbols.Count, truncated, MaxNewAssetsPerCycle);
        }

        if (missing is null || missing.Count == 0)
        {
            return 0;
        }

        // N4: AddRange in one call rather than a foreach+Add loop.
        _context.Assets.AddRange(missing.Select(s => new Asset
        {
            Symbol = s,
            Name = s,
            IsActive = true,
        }));

        int insertedCount;
        try
        {
            insertedCount = await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // NB1: a concurrent writer (overlapping fetcher tick or second app
            // instance) beat us to some of these symbols. Treat as idempotent —
            // refetch the active set, insert only the ones still missing, and
            // report the actual inserted count. If every symbol already exists,
            // return 0 without calling the reconciler or invalidating the cache.
            _logger.LogDebug(
                ex,
                "AssetDiscovery detected concurrent writer on unique(Symbol); reconciling and retrying");
            return await RetryAfterRaceAsync(missing, ct);
        }

        if (insertedCount == 0)
        {
            // SaveChangesAsync with zero affected rows is unexpected here (we
            // just staged AddRange). Be defensive — leave cache/reconciler alone.
            return 0;
        }

        // NB3: feed the reconciler the newly-inserted asset IDs so the cross-
        // join is scoped to the handful of new rows rather than (users × all assets).
        var newAssetIds = await _context.Assets
            .Where(a => missing.Contains(a.Symbol))
            .Select(a => a.Id)
            .ToListAsync(ct);

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(
            _context, newAssetIds, ct);

        _assetRepository.InvalidateCache();

        _logger.LogInformation(
            "Auto-discovered {Count} new assets: {Symbols}",
            missing.Count,
            FormatSymbolListForLog(missing));

        return missing.Count;
    }

    private async Task<int> RetryAfterRaceAsync(
        List<string> originalMissing, CancellationToken ct)
    {
        // Roll back the failed AddRange — the change tracker still believes the
        // entities are pending-insert after a SaveChanges failure.
        foreach (var entry in _context.ChangeTracker.Entries<Asset>()
            .Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }

        _assetRepository.InvalidateCache();
        var refreshed = await _assetRepository.GetActiveAsync();
        var refreshedSymbols = new HashSet<string>(
            refreshed.Select(a => a.Symbol),
            StringComparer.OrdinalIgnoreCase);

        var stillMissing = originalMissing
            .Where(s => !refreshedSymbols.Contains(s))
            .ToList();

        if (stillMissing.Count == 0)
        {
            // Another writer inserted everything. Reconcile once (scoped to
            // our would-be IDs is not possible here — fall back to global).
            return 0;
        }

        _context.Assets.AddRange(stillMissing.Select(s => new Asset
        {
            Symbol = s,
            Name = s,
            IsActive = true,
        }));
        await _context.SaveChangesAsync(ct);

        var newAssetIds = await _context.Assets
            .Where(a => stillMissing.Contains(a.Symbol))
            .Select(a => a.Id)
            .ToListAsync(ct);
        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(
            _context, newAssetIds, ct);

        _assetRepository.InvalidateCache();

        _logger.LogInformation(
            "Auto-discovered {Count} new assets (after concurrent-writer retry): {Symbols}",
            stillMissing.Count,
            FormatSymbolListForLog(stillMissing));

        return stillMissing.Count;
    }

    /// <summary>
    /// Shortens the symbol list when it is very long so the log line does not
    /// become an amplification vector (NB2).
    /// </summary>
    private static string FormatSymbolListForLog(IReadOnlyList<string> symbols)
    {
        if (symbols.Count <= MaxLoggedSymbols)
        {
            return string.Join(", ", symbols);
        }

        var head = string.Join(", ", symbols.Take(MaxLoggedSymbols));
        return $"{head} (+{symbols.Count - MaxLoggedSymbols} more)";
    }

    /// <summary>
    /// Detects a unique-constraint violation inside a <see cref="DbUpdateException"/>.
    /// SQL Server raises SqlException numbers 2601 / 2627; SQLite raises
    /// <c>SqliteException.SqliteErrorCode = 19</c> (SQLITE_CONSTRAINT) with
    /// extended code 2067 (SQLITE_CONSTRAINT_UNIQUE). We detect via the
    /// exception type name and hresult-like number to avoid taking hard
    /// dependencies on the SqlClient / Sqlite provider assemblies here.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            var typeName = inner.GetType().Name;
            if (typeName == "SqlException")
            {
                // SqlException.Number via reflection to keep this provider-agnostic
                var numberProp = inner.GetType().GetProperty("Number");
                if (numberProp?.GetValue(inner) is int number && (number == 2601 || number == 2627))
                {
                    return true;
                }
            }
            else if (typeName == "SqliteException")
            {
                var codeProp = inner.GetType().GetProperty("SqliteErrorCode");
                if (codeProp?.GetValue(inner) is int code && code == 19)
                {
                    return true;
                }
            }

            // Fallback: substring match on the message for other providers.
            var msg = inner.Message ?? string.Empty;
            if (msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
