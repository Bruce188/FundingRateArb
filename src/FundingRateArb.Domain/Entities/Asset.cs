using System.ComponentModel.DataAnnotations;

namespace FundingRateArb.Domain.Entities;

public class Asset
{
    public int Id { get; set; }

    [Required, MaxLength(20)]
    public string Symbol { get; set; } = null!;

    [Required, MaxLength(100)]
    public string Name { get; set; } = null!;

    public bool IsActive { get; set; } = true;

    public ICollection<FundingRateSnapshot> FundingRateSnapshots { get; set; } = [];
    public ICollection<ArbitragePosition> Positions { get; set; } = [];
}
