using FluentAssertions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Tests.Integration.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FundingRateArb.Tests.Integration;

public class BackfillPhantomFeeFlagTests : IDisposable
{
    private readonly TestDbFixture _fixture;
    private readonly ApplicationUser _user;

    public BackfillPhantomFeeFlagTests()
    {
        _fixture = new TestDbFixture();
        _user = new ApplicationUser { Id = Guid.NewGuid().ToString(), UserName = "test@test.com", Email = "test@test.com" };
        _fixture.Context.Users.Add(_user);
        _fixture.Context.SaveChanges();
    }

    private ArbitragePosition BuildPosition(
        PositionStatus status,
        decimal? longFilled = null,
        decimal? shortFilled = null,
        decimal entryFees = 1.5m,
        decimal exitFees = 0.8m,
        decimal? realizedPnl = -2.3m) => new()
    {
        UserId = _user.Id,
        AssetId = _fixture.TestAsset.Id,
        LongExchangeId = _fixture.TestExchange.Id,
        ShortExchangeId = _fixture.TestExchange.Id,
        Status = status,
        SizeUsdc = 500m,
        MarginUsdc = 100m,
        Leverage = 5,
        OpenedAt = DateTime.UtcNow.AddHours(-2),
        LongFilledQuantity = longFilled,
        ShortFilledQuantity = shortFilled,
        EntryFeesUsdc = entryFees,
        ExitFeesUsdc = exitFees,
        RealizedPnl = realizedPnl,
        IsPhantomFeeBackfill = false,
    };

    private static async Task<int> ApplyBackfillAsync(AppDbContext ctx)
    {
        var targets = await ctx.Set<ArbitragePosition>()
            .Where(p => (p.LongFilledQuantity == null || p.LongFilledQuantity == 0m)
                     && (p.ShortFilledQuantity == null || p.ShortFilledQuantity == 0m)
                     && p.Status == PositionStatus.EmergencyClosed
                     && !p.IsPhantomFeeBackfill)
            .ToListAsync();

        foreach (var p in targets)
            p.IsPhantomFeeBackfill = true;

        return await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Backfill_SetsFlag_OnlyOnZeroFillEmergencyClosedRows()
    {
        // (a) zero-fill EmergencyClosed — should be flagged
        var phantom = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0m, shortFilled: 0m);

        // (b) one-leg-filled EmergencyClosed — must NOT be flagged
        var oneLegLong = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0.5m, shortFilled: 0m);
        var oneLegShort = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0m, shortFilled: 0.3m);

        // (c) Open — must NOT be flagged
        var openPos = BuildPosition(PositionStatus.Open, longFilled: 0m, shortFilled: 0m);

        _fixture.Context.AddRange(phantom, oneLegLong, oneLegShort, openPos);
        await _fixture.Context.SaveChangesAsync();

        await ApplyBackfillAsync(_fixture.Context);

        phantom.IsPhantomFeeBackfill.Should().BeTrue();
        oneLegLong.IsPhantomFeeBackfill.Should().BeFalse();
        oneLegShort.IsPhantomFeeBackfill.Should().BeFalse();
        openPos.IsPhantomFeeBackfill.Should().BeFalse();
    }

    [Fact]
    public async Task Backfill_DoesNotMutate_FeeOrPnlColumns()
    {
        var phantom = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0m, shortFilled: 0m,
            entryFees: 1.5m, exitFees: 0.8m, realizedPnl: -2.3m);

        _fixture.Context.Add(phantom);
        await _fixture.Context.SaveChangesAsync();

        await ApplyBackfillAsync(_fixture.Context);

        phantom.EntryFeesUsdc.Should().Be(1.5m);
        phantom.ExitFeesUsdc.Should().Be(0.8m);
        phantom.RealizedPnl.Should().Be(-2.3m);
        phantom.Status.Should().Be(PositionStatus.EmergencyClosed);
    }

    [Fact]
    public async Task Backfill_IsIdempotent_SecondRunChangesZeroRows()
    {
        var phantom = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0m, shortFilled: 0m);

        _fixture.Context.Add(phantom);
        await _fixture.Context.SaveChangesAsync();

        var firstPassRows = await ApplyBackfillAsync(_fixture.Context);
        firstPassRows.Should().Be(1);

        var secondPassRows = await ApplyBackfillAsync(_fixture.Context);
        secondPassRows.Should().Be(0, "idempotent: already-flagged rows are excluded by the WHERE clause");
    }

    [Fact]
    public async Task Backfill_FlagsRowsWithNullFillQuantities()
    {
        var phantomNullBoth = BuildPosition(PositionStatus.EmergencyClosed, longFilled: null, shortFilled: null);
        var phantomNullLong = BuildPosition(PositionStatus.EmergencyClosed, longFilled: null, shortFilled: 0m);
        var phantomNullShort = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0m, shortFilled: null);
        var realFillNullOther = BuildPosition(PositionStatus.EmergencyClosed, longFilled: 0.5m, shortFilled: null);

        _fixture.Context.AddRange(phantomNullBoth, phantomNullLong, phantomNullShort, realFillNullOther);
        await _fixture.Context.SaveChangesAsync();

        await ApplyBackfillAsync(_fixture.Context);

        phantomNullBoth.IsPhantomFeeBackfill.Should().BeTrue("both legs NULL is the historical phantom shape");
        phantomNullLong.IsPhantomFeeBackfill.Should().BeTrue("long NULL + short 0 is also phantom — neither leg filled");
        phantomNullShort.IsPhantomFeeBackfill.Should().BeTrue("long 0 + short NULL is also phantom — neither leg filled");
        realFillNullOther.IsPhantomFeeBackfill.Should().BeFalse("a real fill on one leg means the position was live");
    }

    public void Dispose() => _fixture.Dispose();
}
