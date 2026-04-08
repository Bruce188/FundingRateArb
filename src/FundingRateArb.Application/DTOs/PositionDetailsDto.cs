using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.DTOs;

/// <summary>DTO for the position details view — no domain entity exposed to the Web layer.</summary>
public class PositionDetailsDto
{
    public int Id { get; set; }
    public string AssetSymbol { get; set; } = string.Empty;
    public int AssetId { get; set; }
    public string LongExchangeName { get; set; } = string.Empty;
    public int LongExchangeId { get; set; }
    public string ShortExchangeName { get; set; } = string.Empty;
    public int ShortExchangeId { get; set; }
    public decimal SizeUsdc { get; set; }
    public decimal MarginUsdc { get; set; }
    public int Leverage { get; set; }
    public decimal LongEntryPrice { get; set; }
    public decimal ShortEntryPrice { get; set; }
    public decimal EntrySpreadPerHour { get; set; }
    public decimal CurrentSpreadPerHour { get; set; }
    public decimal AccumulatedFunding { get; set; }
    public decimal? RealizedPnl { get; set; }

    /// <summary>
    /// Closed position PnL decomposition (Analysis Section 4.3.3):
    /// directional component from price movement on actual fill prices.
    /// Null while the position is open or if RealizedPnl is unset.
    /// </summary>
    public decimal? RealizedDirectionalPnl { get; set; }

    /// <summary>Total fees across entry and exit legs (EntryFeesUsdc + ExitFeesUsdc).</summary>
    public decimal TotalFeesUsdc { get; set; }

    /// <summary>Entry fees captured at position open (both legs).</summary>
    public decimal EntryFeesUsdc { get; set; }

    /// <summary>Exit fees captured at position close (both legs). Zero while open.</summary>
    public decimal ExitFeesUsdc { get; set; }

    public PositionStatus Status { get; set; }
    public CloseReason? CloseReason { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? Notes { get; set; }
    public bool IsDryRun { get; set; }

    // Margin utilization fields (populated when margin state is available)
    public decimal? LongMarginUtilizationPct { get; set; }
    public decimal? ShortMarginUtilizationPct { get; set; }
    public decimal? MaxSafeMovePctLong { get; set; }
    public decimal? MaxSafeMovePctShort { get; set; }
    public decimal? CyclesUntilLiquidation { get; set; }
}
