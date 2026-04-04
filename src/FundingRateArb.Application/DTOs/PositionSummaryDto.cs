using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.DTOs;

public class PositionSummaryDto
{
    public int Id { get; set; }
    public string AssetSymbol { get; set; } = null!;
    public string LongExchangeName { get; set; } = null!;
    public string ShortExchangeName { get; set; } = null!;
    public decimal SizeUsdc { get; set; }
    public decimal MarginUsdc { get; set; }
    public decimal EntrySpreadPerHour { get; set; }
    public decimal CurrentSpreadPerHour { get; set; }
    public decimal AccumulatedFunding { get; set; }
    public decimal UnrealizedPnl { get; set; }

    /// <summary>Per-exchange PnL using each exchange's own mark price (margin reference).</summary>
    public decimal ExchangePnl { get; set; }

    /// <summary>Unified PnL using single reference price (true strategy performance).</summary>
    public decimal UnifiedPnl { get; set; }

    /// <summary>Cross-exchange price divergence percentage.</summary>
    public decimal DivergencePct { get; set; }

    public decimal? RealizedPnl { get; set; }
    public PositionStatus Status { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public bool IsDryRun { get; set; }

    /// <summary>Highest urgency level across all active warning conditions.</summary>
    public WarningLevel WarningLevel { get; set; } = WarningLevel.None;

    /// <summary>Active warning types (e.g. SpreadRisk, TimeBased, Loss).</summary>
    public List<WarningType> WarningTypes { get; set; } = new();

    /// <summary>Margin utilization imbalance between long and short legs (0.0 = balanced, 1.0 = fully imbalanced).</summary>
    public decimal? CollateralImbalancePct { get; set; }
}
