namespace FundingRateArb.Application.DTOs;

public class ArbitrageOpportunityDto
{
    public string AssetSymbol { get; set; } = null!;
    public int AssetId { get; set; }
    public string LongExchangeName { get; set; } = null!;
    public int LongExchangeId { get; set; }
    public string ShortExchangeName { get; set; } = null!;
    public int ShortExchangeId { get; set; }
    public decimal LongRatePerHour { get; set; }
    public decimal ShortRatePerHour { get; set; }
    public decimal SpreadPerHour { get; set; }
    public decimal NetYieldPerHour { get; set; }
    public decimal BoostedNetYieldPerHour { get; set; }
    public decimal AnnualizedYield { get; set; }
    public decimal LongVolume24h { get; set; }
    public decimal ShortVolume24h { get; set; }
    public decimal LongMarkPrice { get; set; }
    public decimal ShortMarkPrice { get; set; }

    /// <summary>
    /// Minutes until the next funding settlement for either leg (minimum of the two).
    /// Null if settlement time is unknown for both legs.
    /// </summary>
    public int? MinutesToNextSettlement { get; set; }

    // Prediction fields (populated by SignalEngine when IRatePredictionService is available)
    public decimal? PredictedLongRate { get; set; }
    public decimal? PredictedShortRate { get; set; }
    public decimal? PredictedSpread { get; set; }
    public decimal? PredictionConfidence { get; set; }
    public string? PredictedTrend { get; set; }

    // Leverage-adjusted metrics (populated when tier data is available)
    public int? EffectiveLeverage { get; set; }
    public decimal? ReturnOnCapitalPerHour { get; set; }
    public decimal? AprOnCapital { get; set; }
    public decimal? BreakEvenCycles { get; set; }
}
