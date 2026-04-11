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
    /// Values: "cex", "hyperliquid", "lighter", "aster-v3".
    /// </summary>
    public string ExchangeType { get; set; } = "cex";

    // Masked display values — never shows plain-text credentials
    public string? MaskedApiKey { get; set; }
    public string? MaskedApiSecret { get; set; }
    public string? MaskedWalletAddress { get; set; }
    public string? MaskedPrivateKey { get; set; }
    public string? MaskedApiKeyIndex { get; set; }
    public string? MaskedSubAccountAddress { get; set; }

    /// <summary>
    /// True when an Aster exchange entry has V1 HMAC credentials stored but no V3 keys.
    /// Used to render a migration banner prompting the user to upgrade to V3 Pro API.
    /// </summary>
    public bool HasLegacyV1Credentials { get; set; }
}
