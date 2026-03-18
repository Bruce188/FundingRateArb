namespace FundingRateArb.Application.DTOs;

public class DashboardDto
{
    public bool BotEnabled { get; set; }
    public int OpenPositionCount { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal BestSpread { get; set; }
    public int TotalAlerts { get; set; }
}
