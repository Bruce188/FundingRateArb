namespace FundingRateArb.Application.DTOs;

/// <summary>
/// PnL attribution window for the Status page section 2. The slippage-residual identity
/// is SUM(AccumulatedFunding − EntryFeesUsdc − ExitFeesUsdc − RealizedPnl).
/// </summary>
public record PnlAttributionWindowDto
{
    public string Window { get; init; } = string.Empty;   // "7d" | "30d" | "Lifetime"
    public decimal GrossFunding { get; init; }
    public decimal EntryFees { get; init; }
    public decimal ExitFees { get; init; }
    public decimal SlippageResidual { get; init; }
    public decimal NetRealized { get; init; }
}

/// <summary>Hold-time distribution bucket for the Status page section 3.</summary>
public record HoldTimeBucketDto
{
    public string Bucket { get; init; } = string.Empty;   // "<60s" | "<5m" | "<1h" | "<6h" | ">=6h"
    public int Count { get; init; }
    public int WinCount { get; init; }
    public decimal TotalPnl { get; init; }
}

/// <summary>Recent failed-open event grouped by (asset, exchange-pair) for Status page section 7.</summary>
public record FailedOpenEventDto
{
    public string AssetSymbol { get; init; } = string.Empty;
    public string LongExchangeName { get; init; } = string.Empty;
    public string ShortExchangeName { get; init; } = string.Empty;
    public int Count { get; init; }
    public DateTime LatestAt { get; init; }
}
