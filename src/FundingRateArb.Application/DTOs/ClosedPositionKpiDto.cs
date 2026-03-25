namespace FundingRateArb.Application.DTOs;

/// <summary>
/// Lightweight projection of closed position data used for KPI computation.
/// Avoids loading full entity graphs with navigation properties.
/// </summary>
public class ClosedPositionKpiDto
{
    public decimal? RealizedPnl { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime OpenedAt { get; set; }
    public string AssetSymbol { get; set; } = "Unknown";
    public string LongExchangeName { get; set; } = "?";
    public string ShortExchangeName { get; set; } = "?";
}
