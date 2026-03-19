using System.ComponentModel.DataAnnotations;

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
}
