namespace FundingRateArb.Application.DTOs;

public class PositionAnalyticsDto
{
    public int PositionId { get; set; }
    public string AssetSymbol { get; set; } = null!;
    public string LongExchangeName { get; set; } = null!;
    public string ShortExchangeName { get; set; } = null!;
    public decimal ActualPnl { get; set; }       // RealizedPnl (closed) or AccumulatedFunding (open)
    public decimal ProjectedPnl { get; set; }     // EntrySpreadPerHour * SizeUsdc * HoursHeld
    public decimal PnlDifference { get; set; }    // Actual - Projected
    public decimal HoursHeld { get; set; }
    public decimal EntrySpreadPerHour { get; set; }
    public decimal SizeUsdc { get; set; }
    public bool IsClosed { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    public DateTime? ClosedAt { get; set; }
    public List<HourlySpreadPoint> SpreadHistory { get; set; } = [];
}

public class HourlySpreadPoint
{
    public DateTime HourUtc { get; set; }
    public decimal SpreadPerHour { get; set; }
}

public class PositionAnalyticsSummaryDto
{
    public int PositionId { get; set; }
    public string AssetSymbol { get; set; } = null!;
    public string LongExchangeName { get; set; } = null!;
    public string ShortExchangeName { get; set; } = null!;
    public decimal ActualPnl { get; set; }
    public decimal ProjectedPnl { get; set; }
    public decimal PnlDifference { get; set; }
    public decimal HoursHeld { get; set; }
    public bool IsClosed { get; set; }
    public string StatusLabel { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; }

    /// <summary>Accuracy as percentage: (Actual / Projected) * 100. Null if projected is zero.</summary>
    public decimal? AccuracyPct { get; set; }
}
