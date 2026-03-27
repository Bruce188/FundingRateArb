namespace FundingRateArb.Web.ViewModels;

public class ApiKeyViewModel
{
    public List<ExchangeCredentialItem> Exchanges { get; set; } = [];
    public string? StatusMessage { get; set; }
}

public class ExchangeCredentialItem
{
    public int ExchangeId { get; set; }
    public string ExchangeName { get; set; } = null!;
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Identifies the exchange type for per-exchange form rendering.
    /// Values: "cex", "hyperliquid", "lighter".
    /// </summary>
    public string ExchangeType { get; set; } = "cex";

    // Masked display values — never shows plain-text credentials
    public string? MaskedApiKey { get; set; }
    public string? MaskedWalletAddress { get; set; }
    public string? MaskedApiKeyIndex { get; set; }
    public string? MaskedSubAccountAddress { get; set; }
}
