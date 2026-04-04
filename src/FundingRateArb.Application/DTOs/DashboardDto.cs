namespace FundingRateArb.Application.DTOs;

public class DashboardDto
{
    public bool BotEnabled { get; set; }
    public string OperatingState { get; set; } = "Stopped";
    public int OpenPositionCount { get; set; }
    public int OpeningPositionCount { get; set; }
    public int NeedsAttentionCount { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal BestSpread { get; set; }
    public int TotalAlerts { get; set; }
}
