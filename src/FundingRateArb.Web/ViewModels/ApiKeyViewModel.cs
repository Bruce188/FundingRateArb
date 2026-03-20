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
    /// True for wallet-based exchanges (Lighter, HyperLiquid).
    /// False for API-key exchanges (Aster).
    /// </summary>
    public bool RequiresWallet { get; set; }

    // Masked display values — never shows plain-text credentials
    public string? MaskedApiKey { get; set; }
    public string? MaskedWalletAddress { get; set; }
}
