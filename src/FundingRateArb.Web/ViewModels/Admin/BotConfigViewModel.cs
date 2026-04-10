using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Web.ViewModels.Admin;

public class BotConfigViewModel
{
    [Display(Name = "Bot Enabled")]
    public bool IsEnabled { get; set; }

    [Display(Name = "Operating State")]
    public BotOperatingState OperatingState { get; set; }

    [Required, Range(0.0001, 0.1), Display(Name = "Open Threshold (%/hour)")]
    public decimal? OpenThreshold { get; set; }

    [Required, Range(-0.01, 0.1), Display(Name = "Close Threshold (%/hour)")]
    public decimal? CloseThreshold { get; set; }

    [Required, Range(0.0001, 0.1), Display(Name = "Alert Threshold (%/hour)")]
    public decimal? AlertThreshold { get; set; }

    [Required, Range(1, 1000000), Display(Name = "Total Capital (USDC)")]
    public decimal? TotalCapitalUsdc { get; set; }

    [Required, Range(1, 50), Display(Name = "Default Leverage")]
    public int? DefaultLeverage { get; set; }

    [Required, Range(1, 10), Display(Name = "Max Concurrent Positions")]
    public int? MaxConcurrentPositions { get; set; }

    [Required, Range(0.01, 1.0), Display(Name = "Max Capital Per Position (%)")]
    public decimal? MaxCapitalPerPosition { get; set; }

    [Required, Range(0.05, 0.50), Display(Name = "Stop Loss (%)")]
    public decimal? StopLossPct { get; set; }

    [Required, Range(1, 168), Display(Name = "Max Hold Time (hours)")]
    public int? MaxHoldTimeHours { get; set; }

    [Required, Range(0, 48), Display(Name = "Min Hold Time (hours)")]
    public int? MinHoldTimeHours { get; set; }

    [Required, Range(0.00001, 0.1), Display(Name = "Volume Fraction (liquidity limit)")]
    public decimal? VolumeFraction { get; set; }

    [Required, Range(1, 168), Display(Name = "Max Break-Even Hours")]
    public int? BreakevenHoursMax { get; set; }

    [Required]
    [Display(Name = "Allocation Strategy")]
    public AllocationStrategy? AllocationStrategy { get; set; }

    [Required]
    [Range(1, 20)]
    [Display(Name = "Top N Opportunities")]
    public int? AllocationTopN { get; set; }

    [Required, Range(1, 168), Display(Name = "Fee Amortization (hours)")]
    public int? FeeAmortizationHours { get; set; } = 12;

    [Required, Range(1, 1000000), Display(Name = "Min Position Size (USDC)")]
    public decimal? MinPositionSizeUsdc { get; set; } = 5m;

    [Required, Range(0, 10000000), Display(Name = "Min Volume 24h (USDC)")]
    public decimal? MinVolume24hUsdc { get; set; } = 50_000m;

    [Required, Range(1, 120), Display(Name = "Rate Staleness (minutes)")]
    public int? RateStalenessMinutes { get; set; } = 15;

    [Required, Range(0.01, 1.0), Display(Name = "Daily Drawdown Pause (%)")]
    public decimal? DailyDrawdownPausePct { get; set; } = 0.08m;

    [Required, Range(1, 20), Display(Name = "Consecutive Loss Pause")]
    public int? ConsecutiveLossPause { get; set; } = 3;

    [Required, Range(1, 60), Display(Name = "Funding Window (minutes)")]
    public int? FundingWindowMinutes { get; set; } = 10;

    [Required, Range(0.01, 1.0), Display(Name = "Max Exposure Per Asset (%)")]
    public decimal? MaxExposurePerAsset { get; set; } = 0.5m;

    [Required, Range(0.01, 1.0), Display(Name = "Max Exposure Per Exchange (%)")]
    public decimal? MaxExposurePerExchange { get; set; } = 0.7m;

    [Required, Range(0.5, 100.0), Display(Name = "Target PnL Multiplier")]
    public decimal? TargetPnlMultiplier { get; set; } = 2.0m;

    [Required, Range(5, 1440), Display(Name = "PnL Target Cooldown (minutes)")]
    public int? PnlTargetCooldownMinutes { get; set; } = 30;

    [Display(Name = "Adaptive Hold Enabled")]
    public bool AdaptiveHoldEnabled { get; set; } = true;

    [Display(Name = "Rebalance Enabled")]
    public bool RebalanceEnabled { get; set; }

    [Required, Range(0.0, 0.01), Display(Name = "Rebalance Min Improvement")]
    public decimal? RebalanceMinImprovement { get; set; } = 0.0002m;

    [Required, Range(1, 20), Display(Name = "Max Rebalances Per Cycle")]
    public int? MaxRebalancesPerCycle { get; set; } = 2;

    [Required, Range(1, 50), Display(Name = "Max Leverage Cap")]
    public int MaxLeverageCap { get; set; } = 3;

    [Required, Range(0.1, 0.95), Display(Name = "Margin Utilization Alert (%)")]
    public decimal MarginUtilizationAlertPct { get; set; } = 0.70m;
}
