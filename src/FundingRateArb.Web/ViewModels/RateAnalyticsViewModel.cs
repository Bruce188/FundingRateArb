using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class RateAnalyticsViewModel
{
    public List<RateTrendDto> Trends { get; set; } = [];
    public List<ZScoreAlertDto> Alerts { get; set; } = [];
    public int? SelectedAssetId { get; set; }
    public int SelectedDays { get; set; } = 7;
    public List<AssetOption> AvailableAssets { get; set; } = [];
}

public class AssetOption
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
}

public class CorrelationViewModel
{
    public List<CorrelationPairDto> Correlations { get; set; } = [];
    public int AssetId { get; set; }
    public string AssetSymbol { get; set; } = string.Empty;
    public int Days { get; set; } = 7;
}
