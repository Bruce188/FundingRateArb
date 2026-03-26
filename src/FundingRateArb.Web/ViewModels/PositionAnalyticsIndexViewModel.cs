using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class PositionAnalyticsIndexViewModel
{
    public List<PositionAnalyticsSummaryDto> Summaries { get; set; } = [];
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
    public bool HasMore { get; set; }

    // Summary KPIs
    public decimal TotalRealizedPnl { get; set; }
    public decimal TotalRealizedPnl7d { get; set; }
    public decimal TotalRealizedPnl30d { get; set; }
    public decimal WinRate { get; set; }
    public int TotalTrades { get; set; }
    public decimal AvgHoldTimeHours { get; set; }
    public decimal AvgPnlPerTrade { get; set; }
    public decimal BestTradePnl { get; set; }
    public decimal WorstTradePnl { get; set; }

    // Per-asset breakdown
    public List<AssetPerformance> PerAsset { get; set; } = [];

    // Per-exchange pair breakdown
    public List<ExchangePairPerformance> PerExchangePair { get; set; } = [];
}

public class AssetPerformance
{
    public string AssetSymbol { get; set; } = null!;
    public int Trades { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal WinRate { get; set; }
    public decimal AvgPnl { get; set; }
}

public class ExchangePairPerformance
{
    public string Pair { get; set; } = null!;
    public int Trades { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal WinRate { get; set; }
}
