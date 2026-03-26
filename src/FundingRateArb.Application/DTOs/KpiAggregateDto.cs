namespace FundingRateArb.Application.DTOs;

/// <summary>
/// Pre-computed scalar KPIs aggregated at the SQL level via GROUP BY.
/// Avoids materializing thousands of rows for in-memory computation.
/// </summary>
public class KpiAggregateDto
{
    public int TotalTrades { get; set; }
    public int WinCount { get; set; }
    public decimal TotalPnl { get; set; }
    public decimal Pnl7d { get; set; }
    public decimal Pnl30d { get; set; }
    public decimal BestPnl { get; set; }
    public decimal WorstPnl { get; set; }
    public double TotalHoldHours { get; set; }

    /// <summary>
    /// Denominator for AvgHoldTimeHours — equals TotalTrades when below the 10K cap.
    /// Capped at 10,000 for memory safety when computing TotalHoldHours.
    /// </summary>
    public int HoldDataCount { get; set; }
}

/// <summary>
/// Per-asset KPI breakdown aggregated at the SQL level.
/// </summary>
public class AssetKpiAggregateDto
{
    public string AssetSymbol { get; set; } = "Unknown";
    public int Trades { get; set; }
    public int WinCount { get; set; }
    public decimal TotalPnl { get; set; }
}

/// <summary>
/// Per-exchange-pair KPI breakdown aggregated at the SQL level.
/// </summary>
public class ExchangePairKpiAggregateDto
{
    public string LongExchangeName { get; set; } = "?";
    public string ShortExchangeName { get; set; } = "?";
    public int Trades { get; set; }
    public int WinCount { get; set; }
    public decimal TotalPnl { get; set; }
}
