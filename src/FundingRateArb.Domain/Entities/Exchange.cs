using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class Exchange
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = null!;

    [Required, MaxLength(255)]
    public string ApiBaseUrl { get; set; } = null!;

    [Required, MaxLength(255)]
    public string WsBaseUrl { get; set; } = null!;

    public FundingInterval FundingInterval { get; set; }
    public int FundingIntervalHours { get; set; } = 1;
    public bool SupportsSubAccounts { get; set; }
    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Description { get; set; }

    public ICollection<FundingRateSnapshot> FundingRateSnapshots { get; set; } = [];
    public ICollection<ArbitragePosition> LongPositions { get; set; } = [];
    public ICollection<ArbitragePosition> ShortPositions { get; set; } = [];
}
