using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class BotConfiguration
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; }
    public decimal OpenThreshold { get; set; } = 0.0002m;
    public decimal AlertThreshold { get; set; } = 0.0001m;
    public decimal CloseThreshold { get; set; } = -0.00005m;

    [Range(0.01, 1.0)]
    public decimal StopLossPct { get; set; } = 0.10m;

    public int MaxHoldTimeHours { get; set; } = 48;

    /// <summary>Minimum hours to hold before allowing SpreadCollapsed close. StopLoss always applies.
    /// Capped at 48h regardless of MaxHoldTimeHours — validated by ConfigValidator.</summary>
    [Range(0, 48)]
    public int MinHoldTimeHours { get; set; } = 2;

    public decimal VolumeFraction { get; set; } = 0.001m;

    [Range(0.01, 1.0)]
    public decimal MaxCapitalPerPosition { get; set; } = 0.90m;

    [Range(1, 168)]
    public int BreakevenHoursMax { get; set; } = 8;

    [Range(0.01, (double)decimal.MaxValue)]
    public decimal TotalCapitalUsdc { get; set; } = 39m;

    [Range(1, 125)]
    public int DefaultLeverage { get; set; } = 5;

    [Range(1, 100)]
    public int MaxConcurrentPositions { get; set; } = 1;

    public AllocationStrategy AllocationStrategy { get; set; } = AllocationStrategy.Concentrated;

    [Range(1, 20)]
    public int AllocationTopN { get; set; } = 3;

    // Risk management (decoupled from MaxHoldTimeHours)
    [Range(1, 168)]
    public int FeeAmortizationHours { get; set; } = 12;

    [Range(1, (double)decimal.MaxValue)]
    public decimal MinPositionSizeUsdc { get; set; } = 5m;

    [Range(0, (double)decimal.MaxValue)]
    public decimal MinVolume24hUsdc { get; set; } = 50_000m;

    [Range(1, 120)]
    public int RateStalenessMinutes { get; set; } = 15;

    [Range(0.01, 1.0)]
    public decimal DailyDrawdownPausePct { get; set; } = 0.08m;

    [Range(1, 20)]
    public int ConsecutiveLossPause { get; set; } = 3;

    /// <summary>
    /// Minutes before next funding settlement to boost opportunity scoring.
    /// Opportunities within this window receive a 20% effective yield boost for allocation.
    /// </summary>
    [Range(1, 60)]
    public int FundingWindowMinutes { get; set; } = 10;

    /// <summary>Max fraction of TotalCapitalUsdc in any single asset (e.g., 0.5 = 50%).</summary>
    [Range(0.01, 1.0)]
    public decimal MaxExposurePerAsset { get; set; } = 0.5m;

    /// <summary>Max fraction of TotalCapitalUsdc on any single exchange (e.g., 0.7 = 70%).</summary>
    [Range(0.01, 1.0)]
    public decimal MaxExposurePerExchange { get; set; } = 0.7m;

    /// <summary>Close position when AccumulatedFunding >= TargetPnlMultiplier * estimated_entry_fees.</summary>
    [Range(0.5, 100.0)]
    public decimal TargetPnlMultiplier { get; set; } = 2.0m;

    /// <summary>Enable PnL-target exit (when false, only MaxHoldTimeHours applies).</summary>
    public bool AdaptiveHoldEnabled { get; set; } = true;

    /// <summary>Enable automatic portfolio rebalancing.</summary>
    public bool RebalanceEnabled { get; set; }

    /// <summary>Minimum spread improvement (per hour) to justify closing an existing position.</summary>
    [Range(0.0, 0.01)]
    public decimal RebalanceMinImprovement { get; set; } = 0.0002m;

    /// <summary>Maximum number of rebalance closes per bot cycle to prevent runaway liquidation.</summary>
    [Range(1, 20)]
    public int MaxRebalancesPerCycle { get; set; } = 2;

    /// <summary>Number of consecutive failures before a per-exchange circuit breaker opens.</summary>
    [Range(1, 20)]
    public int ExchangeCircuitBreakerThreshold { get; set; } = 3;

    /// <summary>Minutes an exchange is excluded after the circuit breaker opens.</summary>
    [Range(1, 120)]
    public int ExchangeCircuitBreakerMinutes { get; set; } = 15;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedByUserId { get; set; } = null!;
}
