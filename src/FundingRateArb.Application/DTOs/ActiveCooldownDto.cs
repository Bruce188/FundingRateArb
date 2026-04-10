namespace FundingRateArb.Application.DTOs;

public class ActiveCooldownDto
{
    public string CooldownKey { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public int RemainingMinutes { get; set; }
}
