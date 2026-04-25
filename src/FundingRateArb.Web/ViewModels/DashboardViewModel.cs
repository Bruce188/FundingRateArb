using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class DashboardViewModel
{
    public bool IsAuthenticated { get; set; }
    public bool BotEnabled { get; set; }
    public string OperatingState { get; set; } = "Stopped";
    public int OpenPositionCount { get; set; }
    public int OpeningPositionCount { get; set; }
    public int NeedsAttentionCount { get; set; }
    public decimal TotalPnl { get; set; }

    /// <summary>Realized PnL total across all closed positions, excluding phantom-fee backfill rows.</summary>
    public decimal TotalRealizedPnl { get; set; }

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

    // When false, the dashboard renders a "data source unavailable" banner.
    public bool DatabaseAvailable { get; set; } = true;

    // System health data — populated for authenticated users only
    public List<ActiveCooldownDto> ActiveCooldowns { get; set; } = [];
    public List<CircuitBreakerStatusDto> CircuitBreakerStates { get; set; } = [];
    public DateTime? LastFundingRateFetch { get; set; }

    /// <summary>Server-rendered initial balance snapshot. Null when aggregator is unavailable.</summary>
    public BalanceSnapshotDto? InitialBalances { get; set; }
}
