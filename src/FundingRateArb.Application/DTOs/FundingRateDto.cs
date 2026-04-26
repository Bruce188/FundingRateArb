namespace FundingRateArb.Application.DTOs;

public class FundingRateDto
{
    public string ExchangeName { get; set; } = null!;
    public string Symbol { get; set; } = null!;
    public decimal RatePerHour { get; set; }
    public decimal RawRate { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal IndexPrice { get; set; }
    public decimal Volume24hUsd { get; set; }

    /// <summary>
    /// Next funding settlement time (UTC) from the exchange API, if available.
    /// Used for settlement-aware funding accumulation on periodic exchanges (e.g. Aster).
    /// </summary>
    public DateTime? NextSettlementUtc { get; set; }

    /// <summary>
    /// Detected funding interval in hours from the exchange API (if available).
    /// Used by FundingRateFetcher to detect interval changes (e.g. Binance 8h to 4h).
    /// </summary>
    public int? DetectedFundingIntervalHours { get; set; }

    /// <summary>
    /// Best bid price from the order book snapshot, if available.
    /// Populated by connectors that provide order-book data alongside funding rates.
    /// </summary>
    public decimal? BestBid { get; set; }

    /// <summary>
    /// Best ask price from the order book snapshot, if available.
    /// Populated by connectors that provide order-book data alongside funding rates.
    /// </summary>
    public decimal? BestAsk { get; set; }
}
