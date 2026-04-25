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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Maps_IsPhantomFeeBackfill_ToSummaryDto_RoundTrip(bool flag)
    {
        var pos = CreatePosition();
        pos.IsPhantomFeeBackfill = flag;

        var dto = pos.ToSummaryDto();

        dto.IsPhantomFeeBackfill.Should().Be(flag,
            "IsPhantomFeeBackfill must be preserved through entity→PositionSummaryDto mapping");
    }

    // ── PositionDetailsDto round-trip ─────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Maps_IsPhantomFeeBackfill_ToDetailsDto_RoundTrip(bool flag)
    {
        var pos = CreatePosition();
        pos.IsPhantomFeeBackfill = flag;

        var dto = pos.ToDetailsDto();

        dto.IsPhantomFeeBackfill.Should().Be(flag,
            "IsPhantomFeeBackfill must be preserved through entity→PositionDetailsDto mapping");
    }
}
