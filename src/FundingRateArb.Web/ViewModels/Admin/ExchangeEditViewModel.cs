using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace FundingRateArb.Web.ViewModels.Admin;

public class ExchangeEditViewModel
{
    public int Id { get; set; }

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

    [Display(Name = "Active")]
    public bool IsActive { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public IEnumerable<SelectListItem>? FundingIntervalOptions { get; set; }
}
