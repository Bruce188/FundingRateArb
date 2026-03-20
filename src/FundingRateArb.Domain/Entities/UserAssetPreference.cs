namespace FundingRateArb.Domain.Entities;

public class UserAssetPreference
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int AssetId { get; set; }
    public bool IsEnabled { get; set; } = true;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
