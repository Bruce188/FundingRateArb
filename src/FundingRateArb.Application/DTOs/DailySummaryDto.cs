namespace FundingRateArb.Application.DTOs;

public class DailySummaryDto
{
    public int OpenPositionCount { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal BestAvailableSpread { get; set; }
    public string? BestOpportunityAsset { get; set; }
    public int AlertsCount { get; set; }
    public int ClosedTodayCount { get; set; }
    public decimal RealizedPnlToday { get; set; }
}
