namespace FundingRateArb.Domain.Entities;

/// <summary>
/// Snapshot of a detected arbitrage opportunity at a specific bot cycle.
/// Enables retrospective analysis of passed opportunities and their outcomes.
/// </summary>
public class OpportunitySnapshot
{
    public int Id { get; set; }
    public int AssetId { get; set; }
    public int LongExchangeId { get; set; }
    public int ShortExchangeId { get; set; }
    public decimal SpreadPerHour { get; set; }
    public decimal NetYieldPerHour { get; set; }
    public decimal LongVolume24h { get; set; }
    public decimal ShortVolume24h { get; set; }
    public bool WasOpened { get; set; }
    public string? SkipReason { get; set; } // "capital_exhausted", "cooldown", "below_threshold", "max_positions"
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public Asset Asset { get; set; } = null!;
    public Exchange LongExchange { get; set; } = null!;
    public Exchange ShortExchange { get; set; } = null!;
}
