namespace FundingRateArb.Domain.Entities;

public class UserExchangePreference
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int ExchangeId { get; set; }
    public bool IsEnabled { get; set; } = true;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Exchange Exchange { get; set; } = null!;
}
