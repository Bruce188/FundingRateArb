using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FundingRateArb.Web.ViewModels;

public class UserConfigViewModel
{
    [Display(Name = "Enable my trading bot")]
    public bool IsEnabled { get; set; }

    [Required, Range(0.00001, 1.0), Display(Name = "Open Threshold (%/hour)")]
    public decimal? OpenThreshold { get; set; }

    [Required, Range(0.00001, 1.0), Display(Name = "Close Threshold (%/hour)")]
    public decimal? CloseThreshold { get; set; }

    [Required, Range(0.00001, 1.0), Display(Name = "Alert Threshold (%/hour)")]
    public decimal? AlertThreshold { get; set; }

    [Required, Range(1, 50), Display(Name = "Default Leverage")]
    public int? DefaultLeverage { get; set; }

    [Required, Range(1, 1000000), Display(Name = "Total Capital (USDC)")]
    public decimal? TotalCapitalUsdc { get; set; }

    [Required, Range(0.01, 1.0), Display(Name = "Max Capital Per Position")]
    public decimal? MaxCapitalPerPosition { get; set; }

    [Required, Range(1, 20), Display(Name = "Max Concurrent Positions")]
    public int? MaxConcurrentPositions { get; set; }

    [Required, Range(0.001, 1.0), Display(Name = "Stop Loss (%)")]
    public decimal? StopLossPct { get; set; }

    [Required, Range(1, 720), Display(Name = "Max Hold Time (hours)")]
    public int? MaxHoldTimeHours { get; set; }

    [Required, Display(Name = "Allocation Strategy")]
    public AllocationStrategy? AllocationStrategy { get; set; }

    [Required, Range(1, 20), Display(Name = "Top N Opportunities")]
    public int? AllocationTopN { get; set; }

    [Required, Range(1, 168), Display(Name = "Fee Amortization (hours)")]
    public decimal? FeeAmortizationHours { get; set; }

    [Required, Range(1, 10000), Display(Name = "Min Position Size (USDC)")]
    public decimal? MinPositionSizeUsdc { get; set; }

    [Required, Range(0, 10000000), Display(Name = "Min Volume 24h (USDC)")]
    public decimal? MinVolume24hUsdc { get; set; }

    [Required, Range(1, 60), Display(Name = "Rate Staleness (minutes)")]
    public int? RateStalenessMinutes { get; set; }

    [Required, Range(0.01, 1.0), Display(Name = "Daily Drawdown Pause (%)")]
    public decimal? DailyDrawdownPausePct { get; set; }

    [Required, Range(1, 20), Display(Name = "Consecutive Loss Pause")]
    public int? ConsecutiveLossPause { get; set; }

    [Required, Range(1, 60), Display(Name = "Funding Window (minutes)")]
    public int? FundingWindowMinutes { get; set; }

    [Display(Name = "Enable Email Notifications")]
    public bool EmailNotificationsEnabled { get; set; }

    [Display(Name = "Email on Critical Alerts")]
    public bool EmailCriticalAlerts { get; set; }

    [Display(Name = "Email Daily Summary")]
    public bool EmailDailySummary { get; set; }

    // Dropdown options for AllocationStrategy
    public List<SelectListItem> AllocationStrategyOptions { get; set; } = [];

    // Status message passed via TempData
    public string? StatusMessage { get; set; }
}
