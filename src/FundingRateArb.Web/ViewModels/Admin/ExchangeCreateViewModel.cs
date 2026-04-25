using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FundingRateArb.Web.ViewModels.Admin;

public class ExchangeCreateViewModel
{
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    [Required, MaxLength(255), Display(Name = "API Base URL")]
    public string ApiBaseUrl { get; set; } = null!;

    [Required, MaxLength(255), Display(Name = "WebSocket Base URL")]
    public string WsBaseUrl { get; set; } = null!;

    [Required, Display(Name = "Funding Interval")]
    public FundingInterval? FundingInterval { get; set; }

    [Display(Name = "Interval Hours"), Range(1, 24)]
    public int? FundingIntervalHours { get; set; }

    [Display(Name = "Supports Sub-Accounts")]
    public bool SupportsSubAccounts { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [Display(Name = "Funding Rebate Rate"), Range(0.0, 1.0)]
    public decimal FundingRebateRate { get; set; }

    [Display(Name = "Timing Deviation (seconds)"), Range(0, 300)]
    public int FundingTimingDeviationSeconds { get; set; }

    [Display(Name = "Notional Price Type")]
    public FundingNotionalPriceType FundingNotionalPriceType { get; set; } = FundingNotionalPriceType.MarkPrice;

    public IEnumerable<SelectListItem>? FundingIntervalOptions { get; set; }
}
