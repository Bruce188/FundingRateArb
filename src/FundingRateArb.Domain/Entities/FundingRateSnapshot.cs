namespace FundingRateArb.Domain.Entities;

public class FundingRateSnapshot
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public int AssetId { get; set; }
    public decimal RatePerHour { get; set; }
    public decimal RawRate { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal IndexPrice { get; set; }
    public decimal Volume24hUsd { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public Exchange Exchange { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
