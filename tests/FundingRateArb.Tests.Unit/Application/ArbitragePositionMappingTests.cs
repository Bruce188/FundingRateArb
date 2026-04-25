using FluentAssertions;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Mapping;

/// <summary>
/// Tests that the IsPhantomFeeBackfill flag correctly round-trips from ArbitragePosition entity
/// to both PositionSummaryDto and PositionDetailsDto via the mapping extensions.
/// </summary>
public class ArbitragePositionMappingTests
{
    private static ArbitragePosition CreatePosition() => new()
    {
        Id = 1,
        UserId = "user-1",
        AssetId = 5,
        LongExchangeId = 10,
        ShortExchangeId = 20,
        SizeUsdc = 1000m,
        MarginUsdc = 500m,
        Leverage = 2,
        LongEntryPrice = 50000m,
        ShortEntryPrice = 50100m,
        EntrySpreadPerHour = 0.001m,
        CurrentSpreadPerHour = 0.0008m,
        AccumulatedFunding = 12.5m,
        RealizedPnl = 25.75m,
        Status = PositionStatus.Closed,
        OpenedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        ClosedAt = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
        Asset = new Asset { Id = 5, Symbol = "BTC", Name = "Bitcoin" },
        LongExchange = new Exchange
        {
            Id = 10,
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hl.com",
            WsBaseUrl = "wss://ws.hl.com",
        },
        ShortExchange = new Exchange
        {
            Id = 20,
            Name = "LighterDEX",
            ApiBaseUrl = "https://api.lighter.xyz",
            WsBaseUrl = "wss://ws.lighter.xyz",
        },
    };

    // ── PositionSummaryDto round-trip ─────────────────────────────────────────

    [Fact]
    public void ToSummaryDto_IsPhantomFeeBackfill_True_RoundTrips()
    {
        var pos = CreatePosition();
        pos.IsPhantomFeeBackfill = true;

        var dto = pos.ToSummaryDto();

        dto.IsPhantomFeeBackfill.Should().BeTrue(
            "IsPhantomFeeBackfill=true must be preserved through entity→PositionSummaryDto mapping");
    }

    [Fact]
    public void ToSummaryDto_IsPhantomFeeBackfill_False_RoundTrips()
    {
        var pos = CreatePosition();
        pos.IsPhantomFeeBackfill = false;

        var dto = pos.ToSummaryDto();

        dto.IsPhantomFeeBackfill.Should().BeFalse(
            "IsPhantomFeeBackfill=false must be preserved through entity→PositionSummaryDto mapping");
    }

    [Fact]
    public void ToSummaryDto_IsPhantomFeeBackfill_DefaultsToFalse()
    {
        // Entity default is false; DTO must also default to false
        var pos = CreatePosition();
        // IsPhantomFeeBackfill is implicitly false (default)

        var dto = pos.ToSummaryDto();

        dto.IsPhantomFeeBackfill.Should().BeFalse(
            "IsPhantomFeeBackfill must default to false when the entity field is unset");
    }

    // ── PositionDetailsDto round-trip ─────────────────────────────────────────

    [Fact]
    public void ToDetailsDto_IsPhantomFeeBackfill_True_RoundTrips()
    {
        var pos = CreatePosition();
        pos.IsPhantomFeeBackfill = true;

        var dto = pos.ToDetailsDto();

        dto.IsPhantomFeeBackfill.Should().BeTrue(
            "IsPhantomFeeBackfill=true must be preserved through entity→PositionDetailsDto mapping");
    }

    [Fact]
    public void ToDetailsDto_IsPhantomFeeBackfill_False_RoundTrips()
    {
        var pos = CreatePosition();
        pos.IsPhantomFeeBackfill = false;

        var dto = pos.ToDetailsDto();

        dto.IsPhantomFeeBackfill.Should().BeFalse(
            "IsPhantomFeeBackfill=false must be preserved through entity→PositionDetailsDto mapping");
    }

    [Fact]
    public void ToDetailsDto_IsPhantomFeeBackfill_DefaultsToFalse()
    {
        var pos = CreatePosition();
        // IsPhantomFeeBackfill is implicitly false (default)

        var dto = pos.ToDetailsDto();

        dto.IsPhantomFeeBackfill.Should().BeFalse(
            "IsPhantomFeeBackfill must default to false when the entity field is unset");
    }
}
