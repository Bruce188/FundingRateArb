using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class ArbitragePosition
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int AssetId { get; set; }
    public int LongExchangeId { get; set; }
    public int ShortExchangeId { get; set; }
    public decimal SizeUsdc { get; set; }
    public decimal MarginUsdc { get; set; }
    public int Leverage { get; set; }
    public decimal LongEntryPrice { get; set; }
    public decimal ShortEntryPrice { get; set; }
    public decimal? LongLiquidationPrice { get; set; }
    public decimal? ShortLiquidationPrice { get; set; }
    public decimal EntrySpreadPerHour { get; set; }
    public decimal CurrentSpreadPerHour { get; set; }
    public decimal AccumulatedFunding { get; set; } = 0m;
    public decimal EntryFeesUsdc { get; set; }
    public decimal ExitFeesUsdc { get; set; }
    public DateTime? ClosingStartedAt { get; set; }
    public decimal? RealizedPnl { get; set; }
    public PositionStatus Status { get; set; } = PositionStatus.Opening;
    public CloseReason? CloseReason { get; set; }

    public bool LongLegClosed { get; set; }
    public bool ShortLegClosed { get; set; }

    /// <summary>True if this position was opened in dry-run (paper trading) mode.</summary>
    public bool IsDryRun { get; set; }

    [MaxLength(200)]
    public string? LongOrderId { get; set; }

    [MaxLength(200)]
    public string? ShortOrderId { get; set; }

    /// <summary>Actual filled quantity on the long exchange (for audit trail).</summary>
    public decimal? LongFilledQuantity { get; set; }

    /// <summary>Actual filled quantity on the short exchange (for audit trail).</summary>
    public decimal? ShortFilledQuantity { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public Exchange LongExchange { get; set; } = null!;
    public Exchange ShortExchange { get; set; } = null!;
    public ICollection<Alert> Alerts { get; set; } = [];
}
