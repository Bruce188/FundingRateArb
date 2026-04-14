using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Reusable helper for reconciling UserAssetPreferences and UserExchangePreferences.
/// Called from DbSeeder at startup and from AssetDiscoveryService at runtime.
/// Lives in Infrastructure because it depends on AppDbContext directly for the
/// raw-SQL fast path — only Infrastructure consumers should call it.
///
/// Concurrency: raw-SQL path (production) is safe for concurrent callers — the
/// NOT EXISTS predicate is evaluated atomically within the INSERT. The LINQ
/// fallback is intended for in-memory tests only; running it in production
/// would load all preference rows into memory (O(users × assets) allocation).
/// </summary>
public static class UserPreferenceReconciler
{
    /// <summary>
    /// Ensures every active user has a UserAssetPreference row for every active asset.
    /// Uses raw SQL on SQL Server / SQLite; falls back to LINQ for in-memory tests.
    /// Reconciler overload scoped to specific asset IDs avoids the full cross-join
    /// when only a handful of assets were just discovered.
    /// </summary>
    public static async Task ReconcileUserAssetPreferencesAsync(
        AppDbContext context, CancellationToken ct = default)
    {
        if (UsesRawSqlPath(context))
        {
            await context.Database.ExecuteSqlRawAsync(@"
                INSERT INTO UserAssetPreferences (UserId, AssetId, IsEnabled)
                SELECT u.Id, a.Id, 1
                FROM AspNetUsers u
                CROSS JOIN Assets a
                WHERE a.IsActive = 1
                  AND NOT EXISTS (
                    SELECT 1 FROM UserAssetPreferences p
                    WHERE p.UserId = u.Id AND p.AssetId = a.Id)", ct);
        }
        else
        {
            GuardLinqFallback(context);

            var userIds = await context.Users.Select(u => u.Id).ToListAsync(ct);
            if (userIds.Count == 0)
            {
                return;
            }

            var activeAssetIds = await context.Assets
                .Where(a => a.IsActive)
                .Select(a => a.Id)
                .ToListAsync(ct);

            var existingAssetPrefs = (await context.UserAssetPreferences
                    .Select(p => new { p.UserId, p.AssetId })
                    .ToListAsync(ct))
                .Select(p => (p.UserId, p.AssetId))
                .ToHashSet();

            var assetPrefsToAdd = userIds
                .SelectMany(userId => activeAssetIds
                    .Where(assetId => !existingAssetPrefs.Contains((userId, assetId)))
                    .Select(assetId => new UserAssetPreference
                    {
                        UserId = userId,
                        AssetId = assetId,
                        IsEnabled = true
                    }))
                .ToList();

            if (assetPrefsToAdd.Count > 0)
            {
                context.UserAssetPreferences.AddRange(assetPrefsToAdd);
                await context.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>
    /// Ensures every active user has a UserAssetPreference row for the given asset IDs.
    /// Scoped variant — avoids the full Users × Assets cross-join when only a subset
    /// of assets was just inserted.
    /// </summary>
    public static async Task ReconcileUserAssetPreferencesAsync(
        AppDbContext context,
        IReadOnlyCollection<int> assetIds,
        CancellationToken ct = default)
    {
        if (assetIds.Count == 0)
        {
            return;
        }

        if (UsesRawSqlPath(context))
        {
            // Build parameterized IN clause: @p0, @p1, ...
            var parameters = new object[assetIds.Count];
            var placeholders = new string[assetIds.Count];
            var i = 0;
            foreach (var id in assetIds)
            {
                parameters[i] = id;
                placeholders[i] = $"{{{i}}}";
                i++;
            }

            var sql = $@"
                INSERT INTO UserAssetPreferences (UserId, AssetId, IsEnabled)
                SELECT u.Id, a.Id, 1
                FROM AspNetUsers u
                CROSS JOIN Assets a
                WHERE a.IsActive = 1
                  AND a.Id IN ({string.Join(",", placeholders)})
                  AND NOT EXISTS (
                    SELECT 1 FROM UserAssetPreferences p
                    WHERE p.UserId = u.Id AND p.AssetId = a.Id)";

            await context.Database.ExecuteSqlRawAsync(sql, parameters, ct);
        }
        else
        {
            GuardLinqFallback(context);

            var userIds = await context.Users.Select(u => u.Id).ToListAsync(ct);
            if (userIds.Count == 0)
            {
                return;
            }

            var idSet = assetIds.ToHashSet();
            var activeAssetIds = await context.Assets
                .Where(a => a.IsActive && idSet.Contains(a.Id))
                .Select(a => a.Id)
                .ToListAsync(ct);

            var existingAssetPrefs = (await context.UserAssetPreferences
                    .Where(p => idSet.Contains(p.AssetId))
                    .Select(p => new { p.UserId, p.AssetId })
                    .ToListAsync(ct))
                .Select(p => (p.UserId, p.AssetId))
                .ToHashSet();

            var assetPrefsToAdd = userIds
                .SelectMany(userId => activeAssetIds
                    .Where(assetId => !existingAssetPrefs.Contains((userId, assetId)))
                    .Select(assetId => new UserAssetPreference
                    {
                        UserId = userId,
                        AssetId = assetId,
                        IsEnabled = true
                    }))
                .ToList();

            if (assetPrefsToAdd.Count > 0)
            {
                context.UserAssetPreferences.AddRange(assetPrefsToAdd);
                await context.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>
    /// Ensures every active user has a UserExchangePreference row for every active exchange.
    /// Uses raw SQL on SQL Server / SQLite; falls back to LINQ for in-memory tests.
    /// </summary>
    public static async Task ReconcileUserExchangePreferencesAsync(
        AppDbContext context, CancellationToken ct = default)
    {
        if (UsesRawSqlPath(context))
        {
            await context.Database.ExecuteSqlRawAsync(@"
                INSERT INTO UserExchangePreferences (UserId, ExchangeId, IsEnabled)
                SELECT u.Id, e.Id, 1
                FROM AspNetUsers u
                CROSS JOIN Exchanges e
                WHERE e.IsActive = 1
                  AND NOT EXISTS (
                    SELECT 1 FROM UserExchangePreferences p
                    WHERE p.UserId = u.Id AND p.ExchangeId = e.Id)", ct);
        }
        else
        {
            GuardLinqFallback(context);

            var userIds = await context.Users.Select(u => u.Id).ToListAsync(ct);
            if (userIds.Count == 0)
            {
                return;
            }

            var activeExchangeIds = await context.Exchanges
                .Where(e => e.IsActive)
                .Select(e => e.Id)
                .ToListAsync(ct);

            var existingExchangePrefs = (await context.UserExchangePreferences
                    .Select(p => new { p.UserId, p.ExchangeId })
                    .ToListAsync(ct))
                .Select(p => (p.UserId, p.ExchangeId))
                .ToHashSet();

            var exchangePrefsToAdd = userIds
                .SelectMany(userId => activeExchangeIds
                    .Where(exchangeId => !existingExchangePrefs.Contains((userId, exchangeId)))
                    .Select(exchangeId => new UserExchangePreference
                    {
                        UserId = userId,
                        ExchangeId = exchangeId,
                        IsEnabled = true
                    }))
                .ToList();

            if (exchangePrefsToAdd.Count > 0)
            {
                context.UserExchangePreferences.AddRange(exchangePrefsToAdd);
                await context.SaveChangesAsync(ct);
            }
        }
    }

    // Provider names are stable assembly-qualified strings — used here to avoid
    // taking optional package references (Microsoft.EntityFrameworkCore.Sqlite
    // and .InMemory) into Infrastructure just for the IsSqlite()/IsInMemory()
    // extension methods. The Infrastructure project only ships the SqlServer
    // provider; tests bring Sqlite and InMemory via their own references.
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";
    private const string InMemoryProvider = "Microsoft.EntityFrameworkCore.InMemory";

    /// <summary>
    /// Returns true when the raw-SQL reconciliation path should be used.
    /// SQL Server and SQLite both support the CROSS JOIN / NOT EXISTS syntax
    /// with <c>IsActive = 1</c> (SQLite stores booleans as integers too).
    /// Any other relational provider falls through to the LINQ branch, which
    /// is guarded below — tighter than blanket <c>IsRelational()</c> so
    /// migrating to Postgres surfaces as a loud failure rather than an O(N×M)
    /// silent regression.
    /// </summary>
    private static bool UsesRawSqlPath(AppDbContext context)
    {
        var provider = context.Database.ProviderName;
        return provider == SqlServerProvider || provider == SqliteProvider;
    }

    /// <summary>
    /// Throws when the LINQ fallback would execute on anything other than the
    /// in-memory provider. The LINQ path materializes every preference row —
    /// acceptable in tests, catastrophic in production. A loud failure beats
    /// a silent degradation.
    /// </summary>
    private static void GuardLinqFallback(AppDbContext context)
    {
        var provider = context.Database.ProviderName ?? "<unknown>";
        if (provider == InMemoryProvider)
        {
            return;
        }

        throw new InvalidOperationException(
            $"UserPreferenceReconciler LINQ fallback invoked on provider '{provider}'. " +
            "The LINQ branch is for in-memory tests only. Add provider-specific raw SQL to " +
            "UsesRawSqlPath() before running reconciliation against this provider.");
    }
}
