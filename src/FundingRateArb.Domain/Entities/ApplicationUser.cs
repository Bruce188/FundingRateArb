using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace FundingRateArb.Domain.Entities;

public class ApplicationUser : IdentityUser
{
    [MaxLength(100)]
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ArbitragePosition> Positions { get; set; } = [];
    public ICollection<Alert> Alerts { get; set; } = [];
    public ICollection<UserExchangeCredential> ExchangeCredentials { get; set; } = [];
    public ICollection<UserConfiguration> Configurations { get; set; } = [];
    public ICollection<UserExchangePreference> ExchangePreferences { get; set; } = [];
    public ICollection<UserAssetPreference> AssetPreferences { get; set; } = [];
}
