namespace FundingRateArb.Domain.Entities;

public class AssetExchangeFundingInterval
{
    public int ExchangeId { get; set; }
    public int AssetId { get; set; }
    public int IntervalHours { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int? SourceSnapshotId { get; set; }

    public Exchange Exchange { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
