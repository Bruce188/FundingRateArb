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
    public decimal? RealizedPnl { get; set; }
    public PositionStatus Status { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
