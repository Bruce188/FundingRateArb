using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Tests.Unit.Services;

public class UserPreferenceReconcilerTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // ── ReconcileUserAssetPreferencesAsync ──────────────────────────────────

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_NoUsers_NoOp()
    {
        await using var context = CreateContext();
        // Seed an active asset — but no users
        context.Assets.Add(new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true });
        await context.SaveChangesAsync();

        // Act — should not throw, and no pref rows created
        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(context);

        var prefs = await context.UserAssetPreferences.ToListAsync();
        prefs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_NewAsset_InsertsRowsForAllUsers()
    {
        await using var context = CreateContext();
        // Seed 3 users and 1 active asset
        context.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" },
            new ApplicationUser { Id = "u3", UserName = "user3" }
        );
        context.Assets.Add(new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true });
        await context.SaveChangesAsync();

        // Act
        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(context);

        var prefs = await context.UserAssetPreferences.ToListAsync();
        prefs.Should().HaveCount(3);
        prefs.Should().OnlyContain(p => p.IsEnabled);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_PartialExisting_OnlyAddsMissing()
    {
        await using var context = CreateContext();
        // Seed 2 users, 2 active assets
        context.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" }
        );
        var asset1 = new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        var asset2 = new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true };
        context.Assets.AddRange(asset1, asset2);
        await context.SaveChangesAsync();

        // Pre-seed 1 pref row (u1 / asset1) with IsEnabled=false
        context.UserAssetPreferences.Add(new UserAssetPreference
        {
            UserId = "u1",
            AssetId = asset1.Id,
            IsEnabled = false
        });
        await context.SaveChangesAsync();

        // Act
        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(context);

        var prefs = await context.UserAssetPreferences.ToListAsync();
        // Expected: 4 rows total (2 users × 2 assets), no duplicates
        prefs.Should().HaveCount(4);

        // Pre-existing row's IsEnabled must remain false (not overwritten)
        var preExisting = prefs.Single(p => p.UserId == "u1" && p.AssetId == asset1.Id);
        preExisting.IsEnabled.Should().BeFalse();
    }

    // ── ReconcileUserExchangePreferencesAsync ───────────────────────────────

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_NoUsers_NoOp()
    {
        await using var context = CreateContext();
        // Seed an active exchange — but no users
        context.Exchanges.Add(new Exchange
        {
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hyperliquid.xyz",
            WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        });
        await context.SaveChangesAsync();

        // Act
        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(context);

        var prefs = await context.UserExchangePreferences.ToListAsync();
        prefs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_NewExchange_InsertsRowsForAllUsers()
    {
        await using var context = CreateContext();
        context.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" },
            new ApplicationUser { Id = "u3", UserName = "user3" }
        );
        context.Exchanges.Add(new Exchange
        {
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hyperliquid.xyz",
            WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        });
        await context.SaveChangesAsync();

        // Act
        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(context);

        var prefs = await context.UserExchangePreferences.ToListAsync();
        prefs.Should().HaveCount(3);
        prefs.Should().OnlyContain(p => p.IsEnabled);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_InactiveAsset_SkipsAsset()
    {
        // NB5: asserts the IsActive filter still engages in the LINQ fallback.
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        context.Assets.AddRange(
            new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true },
            new Asset { Symbol = "OLD", Name = "OldCoin", IsActive = false });
        await context.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(context);

        var prefs = await context.UserAssetPreferences.ToListAsync();
        prefs.Should().HaveCount(1);
        var activeId = (await context.Assets.SingleAsync(a => a.Symbol == "BTC")).Id;
        prefs.Single().AssetId.Should().Be(activeId);
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_InactiveExchange_SkipsExchange()
    {
        // NB5: symmetric exchange coverage.
        await using var context = CreateContext();
        context.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        var active = new Exchange
        {
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hyperliquid.xyz",
            WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        };
        var inactive = new Exchange
        {
            Name = "Retired",
            ApiBaseUrl = "https://retired.example",
            WsBaseUrl = "wss://retired.example/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = false
        };
        context.Exchanges.AddRange(active, inactive);
        await context.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(context);

        var prefs = await context.UserExchangePreferences.ToListAsync();
        prefs.Should().HaveCount(1);
        prefs.Single().ExchangeId.Should().Be(active.Id);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_Scoped_OnlyAffectsNamedAssets()
    {
        // NB3: the scoped overload limits the cross-join to the supplied asset IDs.
        await using var context = CreateContext();
        context.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" });
        var kept = new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        var scoped = new Asset { Symbol = "YZY", Name = "YZY", IsActive = true };
        context.Assets.AddRange(kept, scoped);
        await context.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(
            context, new[] { scoped.Id });

        var prefs = await context.UserAssetPreferences.ToListAsync();
        // Expect exactly 2 rows (2 users × 1 scoped asset) — NOT 4 (no BTC rows).
        prefs.Should().HaveCount(2);
        prefs.Should().OnlyContain(p => p.AssetId == scoped.Id);
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_PartialExisting_OnlyAddsMissing()
    {
        await using var context = CreateContext();
        context.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" }
        );
        var exchange1 = new Exchange
        {
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hyperliquid.xyz",
            WsBaseUrl = "wss://api.hyperliquid.xyz/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        };
        var exchange2 = new Exchange
        {
            Name = "Lighter",
            ApiBaseUrl = "https://api.lighter.xyz",
            WsBaseUrl = "wss://api.lighter.xyz/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        };
        context.Exchanges.AddRange(exchange1, exchange2);
        await context.SaveChangesAsync();

        // Pre-seed 1 pref row (u1 / exchange1) with IsEnabled=false
        context.UserExchangePreferences.Add(new UserExchangePreference
        {
            UserId = "u1",
            ExchangeId = exchange1.Id,
            IsEnabled = false
        });
        await context.SaveChangesAsync();

        // Act
        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(context);

        var prefs = await context.UserExchangePreferences.ToListAsync();
        // Expected: 4 rows total (2 users × 2 exchanges), no duplicates
        prefs.Should().HaveCount(4);

        // Pre-existing row's IsEnabled must remain false (not overwritten)
        var preExisting = prefs.Single(p => p.UserId == "u1" && p.ExchangeId == exchange1.Id);
        preExisting.IsEnabled.Should().BeFalse();
    }
}
