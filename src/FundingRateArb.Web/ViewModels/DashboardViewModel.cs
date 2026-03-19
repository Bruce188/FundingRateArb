using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class DashboardViewModel
{
    public bool BotEnabled { get; set; }
    public int OpenPositionCount { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal BestSpread { get; set; }
    public int TotalUnreadAlerts { get; set; }
    public List<PositionSummaryDto> OpenPositions { get; set; } = [];
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
    public decimal NotionalPerLeg { get; set; }
    public decimal VolumeFraction { get; set; }
}
