using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Tests.Integration.Services;

/// <summary>
/// NB4: exercises the raw-SQL path of <see cref="UserPreferenceReconciler"/>
/// against a real relational provider (SQLite in-memory). The unit tests cover
/// only the LINQ fallback — this suite pins the production INSERT … SELECT …
/// NOT EXISTS statements.
/// </summary>
public class UserPreferenceReconcilerSqlTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public UserPreferenceReconcilerSqlTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private AppDbContext NewContext() => new(_options);

    // ── ReconcileUserAssetPreferencesAsync ──────────────────────────────────

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_SqlPath_NoUsers_NoOp()
    {
        await using var ctx = NewContext();
        ctx.Assets.Add(new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true });
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(ctx);

        (await ctx.UserAssetPreferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_SqlPath_NewAsset_InsertsRowsForAllUsers()
    {
        await using var ctx = NewContext();
        ctx.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" },
            new ApplicationUser { Id = "u3", UserName = "user3" });
        ctx.Assets.Add(new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true });
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(ctx);

        var prefs = await ctx.UserAssetPreferences.ToListAsync();
        prefs.Should().HaveCount(3);
        prefs.Should().OnlyContain(p => p.IsEnabled);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_SqlPath_PartialExisting_OnlyAddsMissing()
    {
        await using var ctx = NewContext();
        ctx.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" });
        var btc = new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        var eth = new Asset { Symbol = "ETH", Name = "Ethereum", IsActive = true };
        ctx.Assets.AddRange(btc, eth);
        await ctx.SaveChangesAsync();
        ctx.UserAssetPreferences.Add(new UserAssetPreference
        {
            UserId = "u1",
            AssetId = btc.Id,
            IsEnabled = false,
        });
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(ctx);

        var prefs = await ctx.UserAssetPreferences.AsNoTracking().ToListAsync();
        prefs.Should().HaveCount(4);
        var preExisting = prefs.Single(p => p.UserId == "u1" && p.AssetId == btc.Id);
        preExisting.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_SqlPath_Idempotent()
    {
        await using var ctx = NewContext();
        ctx.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        ctx.Assets.Add(new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true });
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(ctx);
        var firstCount = await ctx.UserAssetPreferences.CountAsync();

        // Second invocation must not throw (unique constraint) and must not
        // insert duplicates.
        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(ctx);
        var secondCount = await ctx.UserAssetPreferences.CountAsync();

        firstCount.Should().Be(1);
        secondCount.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_SqlPath_InactiveAsset_SkipsAsset()
    {
        await using var ctx = NewContext();
        ctx.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        var active = new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        var inactive = new Asset { Symbol = "OLD", Name = "OldCoin", IsActive = false };
        ctx.Assets.AddRange(active, inactive);
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(ctx);

        var prefs = await ctx.UserAssetPreferences.ToListAsync();
        prefs.Should().HaveCount(1);
        prefs.Single().AssetId.Should().Be(active.Id);
    }

    [Fact]
    public async Task ReconcileUserAssetPreferencesAsync_SqlPath_Scoped_OnlyAffectsNamedAssets()
    {
        // NB3: the scoped overload narrows the cross-join; production cycles with
        // a handful of newly-discovered assets no longer touch the full Assets table.
        await using var ctx = NewContext();
        ctx.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" });
        var kept = new Asset { Symbol = "BTC", Name = "Bitcoin", IsActive = true };
        var scoped = new Asset { Symbol = "YZY", Name = "YZY", IsActive = true };
        ctx.Assets.AddRange(kept, scoped);
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserAssetPreferencesAsync(
            ctx, new[] { scoped.Id });

        var prefs = await ctx.UserAssetPreferences.ToListAsync();
        prefs.Should().HaveCount(2);
        prefs.Should().OnlyContain(p => p.AssetId == scoped.Id);
    }

    // ── ReconcileUserExchangePreferencesAsync ───────────────────────────────

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_SqlPath_NoUsers_NoOp()
    {
        await using var ctx = NewContext();
        ctx.Exchanges.Add(MakeExchange("Hyperliquid"));
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(ctx);

        (await ctx.UserExchangePreferences.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_SqlPath_NewExchange_InsertsRowsForAllUsers()
    {
        await using var ctx = NewContext();
        ctx.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" },
            new ApplicationUser { Id = "u3", UserName = "user3" });
        ctx.Exchanges.Add(MakeExchange("Hyperliquid"));
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(ctx);

        var prefs = await ctx.UserExchangePreferences.ToListAsync();
        prefs.Should().HaveCount(3);
        prefs.Should().OnlyContain(p => p.IsEnabled);
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_SqlPath_PartialExisting_OnlyAddsMissing()
    {
        await using var ctx = NewContext();
        ctx.Users.AddRange(
            new ApplicationUser { Id = "u1", UserName = "user1" },
            new ApplicationUser { Id = "u2", UserName = "user2" });
        var e1 = MakeExchange("Hyperliquid");
        var e2 = MakeExchange("Lighter");
        ctx.Exchanges.AddRange(e1, e2);
        await ctx.SaveChangesAsync();
        ctx.UserExchangePreferences.Add(new UserExchangePreference
        {
            UserId = "u1",
            ExchangeId = e1.Id,
            IsEnabled = false,
        });
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(ctx);

        var prefs = await ctx.UserExchangePreferences.AsNoTracking().ToListAsync();
        prefs.Should().HaveCount(4);
        var preExisting = prefs.Single(p => p.UserId == "u1" && p.ExchangeId == e1.Id);
        preExisting.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_SqlPath_Idempotent()
    {
        await using var ctx = NewContext();
        ctx.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        ctx.Exchanges.Add(MakeExchange("Hyperliquid"));
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(ctx);
        var firstCount = await ctx.UserExchangePreferences.CountAsync();
        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(ctx);
        var secondCount = await ctx.UserExchangePreferences.CountAsync();

        firstCount.Should().Be(1);
        secondCount.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileUserExchangePreferencesAsync_SqlPath_InactiveExchange_SkipsExchange()
    {
        await using var ctx = NewContext();
        ctx.Users.Add(new ApplicationUser { Id = "u1", UserName = "user1" });
        var active = MakeExchange("Hyperliquid");
        var inactive = MakeExchange("Retired");
        inactive.IsActive = false;
        ctx.Exchanges.AddRange(active, inactive);
        await ctx.SaveChangesAsync();

        await UserPreferenceReconciler.ReconcileUserExchangePreferencesAsync(ctx);

        var prefs = await ctx.UserExchangePreferences.ToListAsync();
        prefs.Should().HaveCount(1);
        prefs.Single().ExchangeId.Should().Be(active.Id);
    }

    private static Exchange MakeExchange(string name) => new()
    {
        Name = name,
        ApiBaseUrl = $"https://api.{name.ToLowerInvariant()}.example",
        WsBaseUrl = $"wss://api.{name.ToLowerInvariant()}.example/ws",
        FundingInterval = Domain.Enums.FundingInterval.Hourly,
        FundingIntervalHours = 1,
        IsActive = true,
    };
}
