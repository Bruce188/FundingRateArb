namespace FundingRateArb.Domain.Entities;

public class UserExchangeCredential
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int ExchangeId { get; set; }
    public string? EncryptedApiKey { get; set; }
    public string? EncryptedApiSecret { get; set; }
    public string? EncryptedWalletAddress { get; set; }
    public string? EncryptedPrivateKey { get; set; }
    public string? EncryptedSubAccountAddress { get; set; }
    public string? EncryptedApiKeyIndex { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUpdatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Exchange Exchange { get; set; } = null!;
}
