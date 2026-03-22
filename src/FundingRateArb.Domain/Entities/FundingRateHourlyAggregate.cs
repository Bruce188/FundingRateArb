namespace FundingRateArb.Domain.Entities;

/// <summary>
/// Hourly aggregated funding rate data for extended retention (30 days).
/// Raw snapshots are kept for 48h; these aggregates provide long-term historical data.
/// </summary>
public class FundingRateHourlyAggregate
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public int AssetId { get; set; }

    /// <summary>Hour boundary (truncated to the start of the hour, UTC).</summary>
    public DateTime HourUtc { get; set; }

    public decimal AvgRatePerHour { get; set; }
    public decimal MinRate { get; set; }
    public decimal MaxRate { get; set; }
    public decimal LastRate { get; set; }
    public decimal AvgVolume24hUsd { get; set; }
    public decimal AvgMarkPrice { get; set; }
    public int SampleCount { get; set; }

    public Exchange Exchange { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
