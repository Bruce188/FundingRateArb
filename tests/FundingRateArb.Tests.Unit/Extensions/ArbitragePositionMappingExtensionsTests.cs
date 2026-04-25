using FluentAssertions;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Tests.Unit.Extensions;

public class ArbitragePositionMappingExtensionsTests
{
    private static ArbitragePosition CreatePositionWithNavigationProperties()
    {
        return new ArbitragePosition
        {
            Id = 42,
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
            Status = PositionStatus.Open,
            CloseReason = CloseReason.PnlTargetReached,
            OpenedAt = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            ClosedAt = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            CurrentDivergencePct = 0.15m,
            Notes = "Test position notes",
            Asset = new Asset { Id = 5, Symbol = "BTC", Name = "Bitcoin" },
            LongExchange = new Exchange
            {
                Id = 10,
                Name = "Hyperliquid",
                ApiBaseUrl = "https://api.hl.com",
                WsBaseUrl = "wss://ws.hl.com"
            },
            ShortExchange = new Exchange
            {
                Id = 20,
                Name = "LighterDEX",
                ApiBaseUrl = "https://api.lighter.xyz",
                WsBaseUrl = "wss://ws.lighter.xyz"
            },
        };
    }

    private static ArbitragePosition CreatePositionWithNullNavigationProperties()
    {
        return new ArbitragePosition
        {
            Id = 99,
            UserId = "user-2",
            AssetId = 7,
            LongExchangeId = 15,
            ShortExchangeId = 25,
            SizeUsdc = 2000m,
            MarginUsdc = 1000m,
            Leverage = 3,
            LongEntryPrice = 3000m,
            ShortEntryPrice = 3010m,
            EntrySpreadPerHour = 0.002m,
            CurrentSpreadPerHour = 0.0015m,
            AccumulatedFunding = 5m,
            RealizedPnl = null,
            Status = PositionStatus.Opening,
            OpenedAt = new DateTime(2026, 4, 1, 8, 0, 0, DateTimeKind.Utc),
            Notes = "No nav props",
            // Asset, LongExchange, ShortExchange left as default (null!)
        };
    }

    [Fact]
    public void ToSummaryDto_MapsAllFields_Correctly()
    {
        var pos = CreatePositionWithNavigationProperties();

        var dto = pos.ToSummaryDto();

        dto.Id.Should().Be(42);
        dto.AssetSymbol.Should().Be("BTC");
        dto.LongExchangeName.Should().Be("Hyperliquid");
        dto.ShortExchangeName.Should().Be("LighterDEX");
        dto.SizeUsdc.Should().Be(1000m);
        dto.MarginUsdc.Should().Be(500m);
        dto.EntrySpreadPerHour.Should().Be(0.001m);
        dto.CurrentSpreadPerHour.Should().Be(0.0008m);
        dto.AccumulatedFunding.Should().Be(12.5m);
        dto.UnrealizedPnl.Should().Be(0m); // set by caller with live computed value
        dto.ExchangePnl.Should().Be(0m);
        dto.UnifiedPnl.Should().Be(0m);
        dto.DivergencePct.Should().Be(0.15m);
        dto.RealizedPnl.Should().Be(25.75m);
        dto.Status.Should().Be(PositionStatus.Open);
        dto.OpenedAt.Should().Be(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        dto.ClosedAt.Should().Be(new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc));
        dto.CloseReason.Should().Be(CloseReason.PnlTargetReached);
        dto.CloseReasonDisplayName.Should().Be("Pnl Target Reached");
        dto.WarningLevel.Should().Be(WarningLevel.None);
        dto.WarningTypes.Should().BeEmpty();
    }

    [Fact]
    public void ToSummaryDto_NullNavigationProperties_UsesFallbacks()
    {
        var pos = CreatePositionWithNullNavigationProperties();

        var dto = pos.ToSummaryDto();

        dto.AssetSymbol.Should().Be("Asset #7");
        dto.LongExchangeName.Should().Be("Exchange #15");
        dto.ShortExchangeName.Should().Be("Exchange #25");
        dto.RealizedPnl.Should().BeNull();
        dto.ClosedAt.Should().BeNull();
        dto.Status.Should().Be(PositionStatus.Opening);
        dto.SizeUsdc.Should().Be(2000m);
    }

    [Fact]
    public void ToSummaryDto_PnlFieldsDefaultToZero_SetByCaller()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.AccumulatedFunding = 99.99m;

        var dto = pos.ToSummaryDto();

        // PnL fields default to 0 — computed live by health monitor and set by caller
        dto.UnrealizedPnl.Should().Be(0m);
        dto.ExchangePnl.Should().Be(0m);
        dto.UnifiedPnl.Should().Be(0m);
        dto.AccumulatedFunding.Should().Be(99.99m);
    }

    [Fact]
    public void ToSummaryDto_IncludesDivergencePct()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.CurrentDivergencePct = 0.15m;

        var dto = pos.ToSummaryDto();

        dto.DivergencePct.Should().Be(0.15m);
    }

    [Fact]
    public void ToSummaryDto_NullDivergence_DefaultsToZero()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.CurrentDivergencePct = null;

        var dto = pos.ToSummaryDto();

        dto.DivergencePct.Should().Be(0m);
    }

    [Fact]
    public void ToDetailsDto_MapsAllFields_Correctly()
    {
        var pos = CreatePositionWithNavigationProperties();

        var dto = pos.ToDetailsDto();

        dto.Id.Should().Be(42);
        dto.AssetSymbol.Should().Be("BTC");
        dto.AssetId.Should().Be(5);
        dto.LongExchangeName.Should().Be("Hyperliquid");
        dto.LongExchangeId.Should().Be(10);
        dto.ShortExchangeName.Should().Be("LighterDEX");
        dto.ShortExchangeId.Should().Be(20);
        dto.SizeUsdc.Should().Be(1000m);
        dto.MarginUsdc.Should().Be(500m);
        dto.Leverage.Should().Be(2);
        dto.LongEntryPrice.Should().Be(50000m);
        dto.ShortEntryPrice.Should().Be(50100m);
        dto.EntrySpreadPerHour.Should().Be(0.001m);
        dto.CurrentSpreadPerHour.Should().Be(0.0008m);
        dto.AccumulatedFunding.Should().Be(12.5m);
        dto.RealizedPnl.Should().Be(25.75m);
        dto.Status.Should().Be(PositionStatus.Open);
        dto.CloseReason.Should().Be(CloseReason.PnlTargetReached);
        dto.OpenedAt.Should().Be(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        dto.ClosedAt.Should().Be(new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc));
        dto.CurrentDivergencePct.Should().Be(0.15m);
        dto.Notes.Should().Be("Test position notes");
    }

    [Fact]
    public void ToDetailsDto_NullNavigationProperties_UsesFallbacks()
    {
        var pos = CreatePositionWithNullNavigationProperties();

        var dto = pos.ToDetailsDto();

        dto.AssetSymbol.Should().Be("Asset #7");
        dto.LongExchangeName.Should().Be("Exchange #15");
        dto.ShortExchangeName.Should().Be("Exchange #25");
        dto.RealizedPnl.Should().BeNull();
        dto.ClosedAt.Should().BeNull();
        dto.Status.Should().Be(PositionStatus.Opening);
        dto.SizeUsdc.Should().Be(2000m);
    }

    // ── Closed-position PnL display in summary DTO ──

    [Fact]
    public void ToSummaryDto_ClosedPosition_PopulatesPnlFromRealizedPnl()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.Closed;
        pos.RealizedPnl = -0.1047m;

        var dto = pos.ToSummaryDto();

        dto.UnifiedPnl.Should().Be(-0.1047m);
        dto.ExchangePnl.Should().Be(-0.1047m);
        dto.UnrealizedPnl.Should().Be(0m, "unrealized is always 0 for closed positions");
    }

    [Fact]
    public void ToSummaryDto_EmergencyClosedPosition_PopulatesPnlFromRealizedPnl()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.EmergencyClosed;
        pos.RealizedPnl = -5.25m;

        var dto = pos.ToSummaryDto();

        dto.UnifiedPnl.Should().Be(-5.25m);
        dto.ExchangePnl.Should().Be(-5.25m);
    }

    [Fact]
    public void ToSummaryDto_ClosedPositionWithNullRealizedPnl_DefaultsToZero()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.Closed;
        pos.RealizedPnl = null;

        var dto = pos.ToSummaryDto();

        dto.UnifiedPnl.Should().Be(0m);
        dto.ExchangePnl.Should().Be(0m);
    }

    [Fact]
    public void ToSummaryDto_OpenPositionWithRealizedPnl_PnlFieldsStayZero()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.Open;
        pos.RealizedPnl = 25m;

        var dto = pos.ToSummaryDto();

        dto.UnifiedPnl.Should().Be(0m, "open positions get PnL from health monitor, not RealizedPnl");
        dto.ExchangePnl.Should().Be(0m);
    }

    [Fact]
    public void ToSummaryDto_LiquidatedPosition_PopulatesPnlFromRealizedPnl()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.Liquidated;
        pos.RealizedPnl = -100m;

        var dto = pos.ToSummaryDto();

        dto.UnifiedPnl.Should().Be(-100m);
        dto.ExchangePnl.Should().Be(-100m);
    }

    [Theory]
    [InlineData(PositionStatus.Opening)]
    [InlineData(PositionStatus.Closing)]
    [InlineData(PositionStatus.Failed)]
    public void ToSummaryDto_NonTerminalStatuses_PnlFieldsStayZero(PositionStatus status)
    {
        // Transitional/failed statuses must NOT inherit RealizedPnl — they have no
        // settled PnL yet. Only Closed/EmergencyClosed/Liquidated are terminal.
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = status;
        pos.RealizedPnl = 50m; // should be ignored for non-terminal

        var dto = pos.ToSummaryDto();

        dto.UnifiedPnl.Should().Be(0m);
        dto.ExchangePnl.Should().Be(0m);
    }

    // ── Closed-position three-view PnL decomposition (Section 4.3.3) ──

    [Fact]
    public void RealizedDirectionalPnl_OpenPosition_ReturnsNull()
    {
        var pos = new ArbitragePosition
        {
            RealizedPnl = null,
            AccumulatedFunding = 5m,
            EntryFeesUsdc = 1m,
            ExitFeesUsdc = 1m,
        };

        pos.RealizedDirectionalPnl.Should().BeNull();
    }

    [Fact]
    public void RealizedDirectionalPnl_ClosedPosition_ReturnsRealizedMinusFundingPlusFees()
    {
        // Identity: directional = realized - funding + entry fees + exit fees
        // Backsolves the price-based component from the composite RealizedPnl.
        var pos = new ArbitragePosition
        {
            RealizedPnl = 100m,
            AccumulatedFunding = 30m,
            EntryFeesUsdc = 5m,
            ExitFeesUsdc = 4m,
        };

        // 100 - 30 + 5 + 4 = 79
        pos.RealizedDirectionalPnl.Should().Be(79m);
    }

    [Fact]
    public void RealizedDirectionalPnl_NegativeRealized_ReturnsCorrectDirectional()
    {
        var pos = new ArbitragePosition
        {
            RealizedPnl = -20m,
            AccumulatedFunding = 5m,
            EntryFeesUsdc = 2m,
            ExitFeesUsdc = 3m,
        };

        // -20 - 5 + 2 + 3 = -20
        pos.RealizedDirectionalPnl.Should().Be(-20m);
    }

    [Fact]
    public void TotalFeesUsdc_SumsEntryAndExitFees()
    {
        var pos = new ArbitragePosition
        {
            EntryFeesUsdc = 7.25m,
            ExitFeesUsdc = 3.75m,
        };

        pos.TotalFeesUsdc.Should().Be(11m);
    }

    [Fact]
    public void TotalFeesUsdc_OpenPosition_HasOnlyEntryFees()
    {
        var pos = new ArbitragePosition
        {
            EntryFeesUsdc = 5m,
            ExitFeesUsdc = 0m,
        };

        pos.TotalFeesUsdc.Should().Be(5m);
    }

    [Fact]
    public void ToPnlDecomposition_OpenPosition_ReturnsNull()
    {
        var pos = new ArbitragePosition
        {
            RealizedPnl = null,
            AccumulatedFunding = 10m,
            EntryFeesUsdc = 2m,
        };

        pos.ToPnlDecomposition().Should().BeNull();
    }

    [Fact]
    public void ToPnlDecomposition_ClosedPosition_PopulatesAllThreeComponents()
    {
        var pos = new ArbitragePosition
        {
            RealizedPnl = 50m,
            AccumulatedFunding = 20m,
            EntryFeesUsdc = 3m,
            ExitFeesUsdc = 2m,
        };

        var decomp = pos.ToPnlDecomposition();

        decomp.Should().NotBeNull();
        decomp!.Directional.Should().Be(35m); // 50 - 20 + 3 + 2
        decomp.Funding.Should().Be(20m);
        decomp.Fees.Should().Be(5m);
    }

    [Fact]
    public void PnlDecompositionDto_StrategyComputed_EqualsDirectionalPlusFundingMinusFees()
    {
        var decomp = new FundingRateArb.Application.DTOs.PnlDecompositionDto(
            Directional: 100m, Funding: 30m, Fees: 9m);

        decomp.Strategy.Should().Be(121m); // 100 + 30 - 9
    }

    [Theory]
    [InlineData(100, 30, 5, 4)]
    [InlineData(-20, 5, 2, 3)]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1234.5678, 567.89, 12.34, 6.78)]
    public void RoundTrip_PnlDecompositionStrategyEqualsRealizedPnl(
        double realizedPnl, double accumulatedFunding, double entryFees, double exitFees)
    {
        // Load-bearing identity: for any closed position, decomposing and reassembling
        // strategy PnL must equal the original RealizedPnl. This catches every sign-flip
        // bug in the RealizedDirectionalPnl backsolve formula.
        var pos = new ArbitragePosition
        {
            RealizedPnl = (decimal)realizedPnl,
            AccumulatedFunding = (decimal)accumulatedFunding,
            EntryFeesUsdc = (decimal)entryFees,
            ExitFeesUsdc = (decimal)exitFees,
        };

        var decomp = pos.ToPnlDecomposition();

        decomp.Should().NotBeNull();
        decomp!.Strategy.Should().Be(pos.RealizedPnl!.Value);
    }

    // ── CloseReason mapping to PositionSummaryDto ──

    [Fact]
    public void ToSummaryDto_ClosedPosition_PopulatesCloseReasonAndDisplayName()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.Closed;
        pos.CloseReason = CloseReason.DivergenceCritical;

        var dto = pos.ToSummaryDto();

        dto.CloseReason.Should().Be(CloseReason.DivergenceCritical);
        dto.CloseReasonDisplayName.Should().Be("Divergence Critical");
    }

    [Fact]
    public void ToSummaryDto_OpenPosition_CloseReasonIsNull()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.Status = PositionStatus.Open;
        pos.CloseReason = null;

        var dto = pos.ToSummaryDto();

        dto.CloseReason.Should().BeNull();
        dto.CloseReasonDisplayName.Should().BeNull();
    }

    [Theory]
    [InlineData(CloseReason.StopLoss, "Stop Loss")]
    [InlineData(CloseReason.MaxHoldTimeReached, "Max Hold Time Reached")]
    [InlineData(CloseReason.PnlTargetReached, "Pnl Target Reached")]
    [InlineData(CloseReason.SpreadCollapsed, "Spread Collapsed")]
    [InlineData(CloseReason.FundingFlipped, "Funding Flipped")]
    [InlineData(CloseReason.LiquidationRisk, "Liquidation Risk")]
    [InlineData(CloseReason.ExchangeDrift, "Exchange Drift")]
    [InlineData(CloseReason.StablecoinDepeg, "Stablecoin Depeg")]
    [InlineData(CloseReason.PriceFeedLost, "Price Feed Lost")]
    [InlineData(CloseReason.Rebalanced, "Rebalanced")]
    [InlineData(CloseReason.Manual, "Manual")]
    [InlineData(CloseReason.EmergencyLegFailed, "Emergency Leg Failed")]
    [InlineData(CloseReason.Rotation, "Rotation")]
    public void ToDisplayName_AllReasons_ProducesReadableText(CloseReason reason, string expected)
    {
        ArbitragePositionMappingExtensions.ToDisplayName(reason).Should().Be(expected);
    }

    // ── Task 2.3: PrevDivergencePct + IsDivergenceNarrowing ─────────────────

    [Fact]
    public void Mapping_PreservesPrevDivergencePctAndIsNarrowingFlag()
    {
        // Arrange: position with narrowing divergence
        var pos = CreatePositionWithNavigationProperties();
        pos.CurrentDivergencePct = 1.5m;
        pos.PrevDivergencePct = 3.0m; // was 3.0, now 1.5 → narrowing

        // Act
        var summaryDto = pos.ToSummaryDto();
        var detailsDto = pos.ToDetailsDto();

        // Assert SummaryDto
        summaryDto.PrevDivergencePct.Should().Be(3.0m,
            "SummaryDto must carry PrevDivergencePct from the entity");
        summaryDto.IsDivergenceNarrowing.Should().BeTrue(
            "IsDivergenceNarrowing should be true when current < previous");

        // Assert DetailsDto
        detailsDto.PrevDivergencePct.Should().Be(3.0m,
            "DetailsDto must carry PrevDivergencePct from the entity");
        detailsDto.IsDivergenceNarrowing.Should().BeTrue(
            "IsDivergenceNarrowing should be true when current < previous");
    }

    [Fact]
    public void Mapping_IsNarrowingFalse_WhenDivergenceWidens()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.CurrentDivergencePct = 3.0m;
        pos.PrevDivergencePct = 1.5m; // widening

        var summaryDto = pos.ToSummaryDto();
        var detailsDto = pos.ToDetailsDto();

        summaryDto.IsDivergenceNarrowing.Should().BeFalse(
            "widening divergence must not be flagged as narrowing");
        detailsDto.IsDivergenceNarrowing.Should().BeFalse(
            "widening divergence must not be flagged as narrowing");
    }

    [Fact]
    public void Mapping_IsNarrowingFalse_WhenPrevDivergencePctIsNull()
    {
        var pos = CreatePositionWithNavigationProperties();
        pos.CurrentDivergencePct = 2.0m;
        pos.PrevDivergencePct = null; // no previous value (first cycle)

        var summaryDto = pos.ToSummaryDto();
        var detailsDto = pos.ToDetailsDto();

        summaryDto.IsDivergenceNarrowing.Should().BeFalse(
            "null previous value means no narrowing determination possible");
        detailsDto.IsDivergenceNarrowing.Should().BeFalse(
            "null previous value means no narrowing determination possible");
    }
}
