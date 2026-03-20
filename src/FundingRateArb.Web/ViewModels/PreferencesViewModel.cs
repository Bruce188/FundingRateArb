namespace FundingRateArb.Web.ViewModels;

public class PreferencesViewModel
{
    public List<ExchangePreferenceItem> Exchanges { get; set; } = [];
    public List<AssetPreferenceItem> Assets { get; set; } = [];
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
}

public class ExchangePreferenceItem
{
    public int ExchangeId { get; set; }
    public string ExchangeName { get; set; } = null!;
    public bool IsEnabled { get; set; }
    public bool HasCredentials { get; set; }
}

public class AssetPreferenceItem
{
    public int AssetId { get; set; }
    public string Symbol { get; set; } = null!;
    public string Name { get; set; } = null!;
    public bool IsEnabled { get; set; }
}
