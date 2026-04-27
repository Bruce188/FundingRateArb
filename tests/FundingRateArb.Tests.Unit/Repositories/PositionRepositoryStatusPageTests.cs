using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FundingRateArb.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for the four new Status-page SQL-aggregated methods on PositionRepository,
/// using an in-memory AppDbContext per test to avoid cross-test pollution.
/// </summary>
public class PositionRepositoryStatusPageTests
{
    private static AppDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ApplicationUser SeedUser(AppDbContext ctx)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "u@test.com", Email = "u@test.com" };
        ctx.Users.Add(user);
        return user;
    }

    private static (Exchange e1, Exchange e2, Exchange e3) SeedExchanges(AppDbContext ctx)
    {
        var hl = new Exchange { Name = "Hyperliquid", ApiBaseUrl = "h", WsBaseUrl = "h" };
        var aster = new Exchange { Name = "Aster", ApiBaseUrl = "a", WsBaseUrl = "a" };
        var lighter = new Exchange { Name = "Lighter", ApiBaseUrl = "l", WsBaseUrl = "l" };
        ctx.Exchanges.AddRange(hl, aster, lighter);
        return (hl, aster, lighter);
    }

    private static Asset SeedAsset(AppDbContext ctx, string symbol = "BTC")
    {
        var asset = new Asset { Symbol = symbol, Name = symbol };
        ctx.Assets.Add(asset);
        return asset;
    }

    private static ArbitragePosition BuildPosition(
        string userId, int assetId, int longExchangeId, int shortExchangeId,
        PositionStatus status, DateTime openedAt, DateTime? closedAt = null,
        decimal? realizedPnl = null, decimal accumFunding = 0m,
        decimal entryFees = 0m, decimal exitFees = 0m,
        decimal? longFilled = null, decimal? shortFilled = null)
    {
        return new ArbitragePosition
        {
            UserId = userId,
            AssetId = assetId,
            LongExchangeId = longExchangeId,
            ShortExchangeId = shortExchangeId,
            Status = status,
            SizeUsdc = 100m, MarginUsdc = 100m, Leverage = 1,
            OpenedAt = openedAt,
            ClosedAt = closedAt,
            RealizedPnl = realizedPnl,
            AccumulatedFunding = accumFunding,
            EntryFeesUsdc = entryFees,
            ExitFeesUsdc = exitFees,
            LongFilledQuantity = longFilled,
            ShortFilledQuantity = shortFilled,
        };
    }

    [Fact]
    public async Task GetPnlAttributionWindowsAsync_AggregatesToSqlIdentity()
    {
        await using var ctx = BuildContext();
        var user = SeedUser(ctx);
        var asset = SeedAsset(ctx);
        var (ex1, _, _) = SeedExchanges(ctx);
        await ctx.SaveChangesAsync();

        var now = DateTime.UtcNow;

        // Three closed positions all within the last 7 days:
        // A: funding=100, entry=5, exit=5, realized=85 → slippage residual = 100-5-5-85 = 5
        // B: funding=200, entry=10, exit=10, realized=170 → residual = 10
        // C: funding=300, entry=15, exit=15, realized=255 → residual = 15
        ctx.ArbitragePositions.AddRange(
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.Closed,
                now.AddDays(-1), now.AddHours(-23), 85m, 100m, 5m, 5m),
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.Closed,
                now.AddDays(-2), now.AddDays(-1).AddHours(-22), 170m, 200m, 10m, 10m),
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.Closed,
                now.AddDays(-3), now.AddDays(-2).AddHours(-21), 255m, 300m, 15m, 15m));
        await ctx.SaveChangesAsync();

        var repo = new PositionRepository(ctx);
        var windows = new[] { now.AddDays(-7), now.AddDays(-30), DateTime.MinValue };
        var result = await repo.GetPnlAttributionWindowsAsync(windows);

        // All 3 positions fall within all 3 windows → same aggregate for each.
        result.Should().HaveCount(3);
        foreach (var row in result)
        {
            row.GrossFunding.Should().Be(600m, $"window {row.Window}");
            row.EntryFees.Should().Be(30m, $"window {row.Window}");
            row.ExitFees.Should().Be(30m, $"window {row.Window}");
            row.NetRealized.Should().Be(510m, $"window {row.Window}");
            // SQL identity: SlippageResidual = GrossFunding - EntryFees - ExitFees - NetRealized
            row.SlippageResidual.Should().Be(row.GrossFunding - row.EntryFees - row.ExitFees - row.NetRealized,
                $"slippage residual identity must hold for window {row.Window}");
            row.SlippageResidual.Should().Be(30m, $"window {row.Window}");
        }
    }

    [Fact]
    public async Task GetPnlAttributionWindowsAsync_NullRealizedPnl_StillContributesToSlippageResidual()
    {
        // Regression: operator-precedence bug previously zeroed the residual contribution
        // for any row where RealizedPnl IS NULL (e.g. zero-fill emergency closes).
        // The fix: coalesce on the leaf → (p.RealizedPnl ?? 0m), so the row's
        // funding/fees still count toward the residual.
        await using var ctx = BuildContext();
        var user = SeedUser(ctx);
        var asset = SeedAsset(ctx);
        var (ex1, _, _) = SeedExchanges(ctx);
        await ctx.SaveChangesAsync();

        var now = DateTime.UtcNow;

        // EmergencyClosed row with null RealizedPnl (zero-fill emergency close).
        // accumFunding=50, entryFees=5, exitFees=5, realizedPnl=null
        // Expected residual contribution = 50 - 5 - 5 - 0 = 40 (coalesces null→0).
        ctx.ArbitragePositions.Add(BuildPosition(
            user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.EmergencyClosed,
            now.AddDays(-1), now.AddHours(-23),
            realizedPnl: null, accumFunding: 50m, entryFees: 5m, exitFees: 5m,
            longFilled: 0m, shortFilled: 0m));
        await ctx.SaveChangesAsync();

        var repo = new PositionRepository(ctx);
        var windows = new[] { now.AddDays(-7), now.AddDays(-30), DateTime.MinValue };
        var result = await repo.GetPnlAttributionWindowsAsync(windows);

        result.Should().HaveCount(3);
        foreach (var row in result)
        {
            // The null-RealizedPnl row must contribute its funding/fees to the residual.
            row.GrossFunding.Should().Be(50m, $"window {row.Window}");
            row.EntryFees.Should().Be(5m, $"window {row.Window}");
            row.ExitFees.Should().Be(5m, $"window {row.Window}");
            row.NetRealized.Should().Be(0m, $"null RealizedPnl coalesces to 0 for window {row.Window}");
            row.SlippageResidual.Should().Be(40m,
                $"residual identity (50-5-5-0=40) must hold even with null RealizedPnl for window {row.Window}");
        }
    }

    [Fact]
    public async Task GetHoldTimeBucketsAsync_BucketsCorrectlyOnExclusiveUpperBounds()
    {
        await using var ctx = BuildContext();
        var user = SeedUser(ctx);
        var asset = SeedAsset(ctx);
        var (ex1, _, _) = SeedExchanges(ctx);
        await ctx.SaveChangesAsync();

        var anchor = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // 10 positions with deterministic hold times: 30s, 59s → <60s (2)
        //   60s, 4m → <5m (2); 5m, 30m → <1h (2); 1h, 5h → <6h (2); 6h, 12h → >=6h (2)
        var holdSeconds = new[] { 30, 59, 60, 240, 300, 1800, 3600, 18000, 21600, 43200 };
        foreach (var secs in holdSeconds)
        {
            var openedAt = anchor;
            var closedAt = anchor.AddSeconds(secs);
            ctx.ArbitragePositions.Add(BuildPosition(
                user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.Closed,
                openedAt, closedAt, realizedPnl: 1m)); // all wins
            anchor = anchor.AddDays(1); // distinct timestamps per position
        }
        await ctx.SaveChangesAsync();

        var repo = new PositionRepository(ctx);
        var buckets = await repo.GetHoldTimeBucketsAsync();

        buckets.Should().HaveCount(5);
        var dict = buckets.ToDictionary(b => b.Bucket);

        dict["<60s"].Count.Should().Be(2, "30s and 59s are both < 60s");
        dict["<5m"].Count.Should().Be(2, "60s and 4m are >= 60s but < 5m");
        dict["<1h"].Count.Should().Be(2, "5m and 30m are >= 5m but < 1h");
        dict["<6h"].Count.Should().Be(2, "1h and 5h are >= 1h but < 6h");
        dict[">=6h"].Count.Should().Be(2, "6h and 12h are >= 6h");

        // All 10 are wins
        buckets.Sum(b => b.WinCount).Should().Be(10);
    }

    [Fact]
    public async Task CountEmergencyClosedZeroFillSinceAsync_DistinguishesFromPhantomFeeRows()
    {
        await using var ctx = BuildContext();
        var user = SeedUser(ctx);
        var asset = SeedAsset(ctx);
        var (ex1, _, _) = SeedExchanges(ctx);
        await ctx.SaveChangesAsync();

        var now = DateTime.UtcNow;

        ctx.ArbitragePositions.AddRange(
            // (a) EmergencyClosed + zero fills — should be counted
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.EmergencyClosed,
                now.AddHours(-1), now, longFilled: 0m, shortFilled: 0m),
            // (b) Failed + null order IDs — NOT counted (this is the Feature 5 metric)
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.Failed,
                now.AddHours(-2), now),
            // (c) EmergencyClosed with non-zero fills — NOT counted
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.EmergencyClosed,
                now.AddHours(-3), now, longFilled: 10m, shortFilled: 10m),
            // (d) Closed with zero fills — NOT counted (wrong status)
            BuildPosition(user.Id, asset.Id, ex1.Id, ex1.Id, PositionStatus.Closed,
                now.AddHours(-4), now, longFilled: 0m, shortFilled: 0m));
        await ctx.SaveChangesAsync();

        var repo = new PositionRepository(ctx);
        var count = await repo.CountEmergencyClosedZeroFillSinceAsync(now.AddDays(-1));

        count.Should().Be(1, "only the EmergencyClosed + zero-fill case (a) qualifies");
    }

    [Fact]
    public async Task GetRecentFailedOpensAsync_GroupsByExchangePairAndProjectsNames()
    {
        await using var ctx = BuildContext();
        var user = SeedUser(ctx);
        var btc = SeedAsset(ctx, "BTC");
        var eth = SeedAsset(ctx, "ETH");
        var (hl, aster, lighter) = SeedExchanges(ctx);
        await ctx.SaveChangesAsync();

        var now = DateTime.UtcNow;

        // 4 failed positions: pair (hl, aster) × 2, pair (hl, lighter) × 1, pair (aster, lighter) × 1
        ctx.ArbitragePositions.AddRange(
            BuildPosition(user.Id, btc.Id, hl.Id, aster.Id, PositionStatus.Failed, now.AddHours(-1)),
            BuildPosition(user.Id, btc.Id, hl.Id, aster.Id, PositionStatus.Failed, now.AddHours(-2)),
            BuildPosition(user.Id, btc.Id, hl.Id, lighter.Id, PositionStatus.Failed, now.AddHours(-3)),
            BuildPosition(user.Id, eth.Id, aster.Id, lighter.Id, PositionStatus.Failed, now.AddHours(-4)));
        await ctx.SaveChangesAsync();

        var repo = new PositionRepository(ctx);
        var events = await repo.GetRecentFailedOpensAsync(now.AddDays(-1));

        events.Should().HaveCount(3, "3 distinct (asset, long, short) groups");

        var hlAster = events.First(e => e.LongExchangeName == "Hyperliquid" && e.ShortExchangeName == "Aster");
        hlAster.Count.Should().Be(2, "(hl, aster) pair has 2 failures");
        hlAster.AssetSymbol.Should().Be("BTC");

        var hlLighter = events.First(e => e.LongExchangeName == "Hyperliquid" && e.ShortExchangeName == "Lighter");
        hlLighter.Count.Should().Be(1);

        var asterLighter = events.First(e => e.LongExchangeName == "Aster" && e.ShortExchangeName == "Lighter");
        asterLighter.Count.Should().Be(1);

        // Names must be resolved, not raw IDs.
        events.Should().NotContain(e => e.LongExchangeName.StartsWith('#'));
        events.Should().NotContain(e => e.ShortExchangeName.StartsWith('#'));
    }
}
