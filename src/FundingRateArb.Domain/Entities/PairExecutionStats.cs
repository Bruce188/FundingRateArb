using System.ComponentModel.DataAnnotations;

namespace FundingRateArb.Domain.Entities;

/// <summary>
/// Per-direction rolling-window execution stats consumed by the deny-list mechanism.
/// One row per <c>(LongExchangeName, ShortExchangeName)</c> tuple. Refreshed per-cycle by
/// <c>BotOrchestrator</c>; consulted by <c>SignalEngine</c> via
/// <c>IPairDenyListSnapshot</c> (refreshed on the same cadence, atomic-swap semantics).
/// </summary>
public class PairExecutionStats
{
    public int Id { get; set; }

    /// <summary>Exchange name of the long leg. Stored as-is (matches the source
    /// <c>ExchangePairKpiAggregateDto.LongExchangeName</c> shape from <c>IPositionRepository.GetPerExchangePairKpiAsync</c>).</summary>
    [Required, MaxLength(64)]
    public string LongExchangeName { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string ShortExchangeName { get; set; } = string.Empty;

    /// <summary>Start of the rolling window — typically <c>NOW − 14d</c> at upsert time.</summary>
    public DateTime WindowStart { get; set; }

    /// <summary>End of the rolling window — typically <c>NOW</c> at upsert time.</summary>
    public DateTime WindowEnd { get; set; }

    public int CloseCount { get; set; }
    public int WinCount { get; set; }
    public decimal TotalPnlUsdc { get; set; }
    public int AvgHoldSec { get; set; }

    /// <summary>True when this direction is currently denied. Read-side check is
    /// <c>IsDenied AND (DeniedUntil IS NULL OR DeniedUntil &gt; NOW)</c>.</summary>
    public bool IsDenied { get; set; }

    /// <summary>Null = indefinite (manual deny without an expiry). Auto-denies always set
    /// <c>NOW + 7d</c>. Strict inequality on the read side: <c>DeniedUntil == NOW</c> is allowed.</summary>
    public DateTime? DeniedUntil { get; set; }

    /// <summary>"auto: 0-win streak" or "manual: &lt;userId&gt;". Used by the per-cycle expiry sweep
    /// to distinguish auto vs manual denies — only auto-denies are auto-cleared on expiry.</summary>
    [MaxLength(128)]
    public string? DeniedReason { get; set; }

    public DateTime LastUpdatedAt { get; set; }
}
