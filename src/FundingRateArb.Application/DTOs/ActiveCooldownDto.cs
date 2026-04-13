namespace FundingRateArb.Application.DTOs;

public class ActiveCooldownDto
{
    public string CooldownKey { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public int RemainingMinutes { get; set; }

    public string? AssetSymbol { get; set; }
    public string? LongExchangeName { get; set; }
    public string? ShortExchangeName { get; set; }
    public string DisplayName => AssetSymbol is not null
        ? $"{AssetSymbol} {LongExchangeName}/{ShortExchangeName}"
        : CooldownKey;
}
