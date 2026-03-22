using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class BotConfiguration
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; } = false;
    public decimal OpenThreshold { get; set; } = 0.0003m;
    public decimal AlertThreshold { get; set; } = 0.0001m;
    public decimal CloseThreshold { get; set; } = -0.00005m;

    [Range(0.01, 1.0)]
    public decimal StopLossPct { get; set; } = 0.15m;

    public int MaxHoldTimeHours { get; set; } = 72;
    public decimal VolumeFraction { get; set; } = 0.001m;

    [Range(0.01, 1.0)]
    public decimal MaxCapitalPerPosition { get; set; } = 0.80m;

    [Range(1, 168)]
    public int BreakevenHoursMax { get; set; } = 6;

    [Range(0.01, (double)decimal.MaxValue)]
    public decimal TotalCapitalUsdc { get; set; } = 107m;

    [Range(1, 125)]
    public int DefaultLeverage { get; set; } = 5;

    [Range(1, 100)]
    public int MaxConcurrentPositions { get; set; } = 1;

    public AllocationStrategy AllocationStrategy { get; set; } = AllocationStrategy.Concentrated;

    [Range(1, 20)]
    public int AllocationTopN { get; set; } = 3;

    // Risk management (decoupled from MaxHoldTimeHours)
    [Range(1, 168)]
    public int FeeAmortizationHours { get; set; } = 24;

    [Range(1, (double)decimal.MaxValue)]
    public decimal MinPositionSizeUsdc { get; set; } = 10m;

    [Range(0, (double)decimal.MaxValue)]
    public decimal MinVolume24hUsdc { get; set; } = 50_000m;

    [Range(1, 120)]
    public int RateStalenessMinutes { get; set; } = 15;

    [Range(0.01, 1.0)]
    public decimal DailyDrawdownPausePct { get; set; } = 0.05m;

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
    public decimal TargetPnlMultiplier { get; set; } = 2.0m;

    /// <summary>Enable PnL-target exit (when false, only MaxHoldTimeHours applies).</summary>
    public bool AdaptiveHoldEnabled { get; set; }

    /// <summary>Enable automatic portfolio rebalancing.</summary>
    public bool RebalanceEnabled { get; set; }

    /// <summary>Minimum spread improvement (per hour) to justify closing an existing position.</summary>
    public decimal RebalanceMinImprovement { get; set; } = 0.0002m;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedByUserId { get; set; } = null!;
}
