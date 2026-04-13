using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Reusable helper for reconciling UserAssetPreferences and UserExchangePreferences.
/// Called from DbSeeder at startup and from AssetDiscoveryService at runtime.
/// Lives in Infrastructure because it depends on AppDbContext directly for the
/// raw-SQL fast path — only Infrastructure consumers should call it.
/// </summary>
public static class UserPreferenceReconciler
{
    /// <summary>
    /// Ensures every active user has a UserAssetPreference row for every active asset.
    /// Uses raw SQL on relational providers; falls back to LINQ for in-memory (tests).
    /// </summary>
    public static async Task ReconcileUserAssetPreferencesAsync(
        AppDbContext context, CancellationToken ct = default)
    {
        if (context.Database.IsRelational())
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
    /// Ensures every active user has a UserExchangePreference row for every active exchange.
    /// Uses raw SQL on relational providers; falls back to LINQ for in-memory (tests).
    /// </summary>
    public static async Task ReconcileUserExchangePreferencesAsync(
        AppDbContext context, CancellationToken ct = default)
    {
        if (context.Database.IsRelational())
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
}
