using System.ComponentModel.DataAnnotations;

namespace FundingRateArb.Web.ViewModels.Admin;

public class AssetCreateViewModel
{
    [Required(ErrorMessage = "Symbol is required")]
    [MaxLength(20)]
    public string Symbol { get; set; } = null!;

    [Required(ErrorMessage = "Name is required")]
    [MaxLength(100)]
    public string Name { get; set; } = null!;

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "Min Notional (USDC)"), Range(1, 1000000)]
    public decimal? MinNotionalUsdc { get; set; }

    [Display(Name = "Max Leverage Hyperliquid"), Range(1, 50)]
    public int? MaxLeverageHyperliquid { get; set; }

    [Display(Name = "Max Leverage Lighter"), Range(1, 50)]
    public int? MaxLeverageLighter { get; set; }

    [Display(Name = "Max Leverage Aster"), Range(1, 50)]
    public int? MaxLeverageAster { get; set; }
}
