namespace FundingRateArb.Domain.Entities;

public class CoinGlassExchangeRate
{
    public long Id { get; set; }
    public DateTime SnapshotTime { get; set; }
    public string SourceExchange { get; set; } = null!;
    public string Symbol { get; set; } = null!;
    public decimal RawRate { get; set; }
    public decimal RatePerHour { get; set; }
    public int IntervalHours { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal IndexPrice { get; set; }
    public decimal Volume24hUsd { get; set; }
}
