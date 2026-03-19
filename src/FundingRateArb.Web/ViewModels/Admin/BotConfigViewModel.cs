using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Web.ViewModels.Admin;

public class BotConfigViewModel
{
    [Display(Name = "Bot Enabled")]
    public bool IsEnabled { get; set; }

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
    public int? FeeAmortizationHours { get; set; } = 24;

    [Required, Range(1, 1000000), Display(Name = "Min Position Size (USDC)")]
    public decimal? MinPositionSizeUsdc { get; set; } = 10m;

    [Required, Range(0, 10000000), Display(Name = "Min Volume 24h (USDC)")]
    public decimal? MinVolume24hUsdc { get; set; } = 50_000m;

    [Required, Range(1, 120), Display(Name = "Rate Staleness (minutes)")]
    public int? RateStalenessMinutes { get; set; } = 15;

    [Required, Range(0.01, 1.0), Display(Name = "Daily Drawdown Pause (%)")]
    public decimal? DailyDrawdownPausePct { get; set; } = 0.05m;

    [Required, Range(1, 20), Display(Name = "Consecutive Loss Pause")]
    public int? ConsecutiveLossPause { get; set; } = 3;
}
