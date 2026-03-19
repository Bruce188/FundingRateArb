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
    public PositionStatus Status { get; set; }
    public CloseReason? CloseReason { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? Notes { get; set; }
}
