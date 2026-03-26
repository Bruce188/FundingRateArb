using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class DashboardViewModel
{
    public bool IsAuthenticated { get; set; }
    public bool BotEnabled { get; set; }
    public int OpenPositionCount { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal BestSpread { get; set; }
    public int TotalUnreadAlerts { get; set; }
    public List<PositionSummaryDto> OpenPositions { get; set; } = [];
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
    public decimal NotionalPerLeg { get; set; }
    public decimal VolumeFraction { get; set; }
    public PipelineDiagnosticsDto? Diagnostics { get; set; }

    // Trading intelligence display flags
    public bool AdaptiveHoldEnabled { get; set; }
    public bool RebalanceEnabled { get; set; }

    // Per-position PnL progress: keyed by position ID
    public Dictionary<int, decimal> PnlProgressByPosition { get; set; } = new();
}
