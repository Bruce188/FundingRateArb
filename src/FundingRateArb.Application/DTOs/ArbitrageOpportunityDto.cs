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

    /// <summary>
    /// Maximum of the two legs' <c>FundingTimingDeviationSeconds</c>, clamped to 0..300.
    /// Used by <see cref="FundingRateArb.Infrastructure.BackgroundServices.BotOrchestrator"/> to decide whether
    /// to skip entry near a settlement boundary. Null if neither leg has settlement timing data.
    /// </summary>
    public int? MaxLegFundingDeviationSeconds { get; set; }

    /// <summary>
    /// Earliest of the two legs' next funding settlement timestamps. Used together with
    /// <see cref="MaxLegFundingDeviationSeconds"/> for the orchestrator's deviation-proximity gate.
    /// Null if no leg's settlement time is cached.
    /// </summary>
    public DateTime? EarliestLegNextSettlementUtc { get; set; }

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

    /// <summary>Hours until entry costs are recovered by funding income. Null if net yield <= 0.</summary>
    public decimal? BreakEvenHours { get; set; }

    /// <summary>True if funding spread has not been favorable for MinConsecutiveFavorableCycles.</summary>
    public bool TrendUnconfirmed { get; set; }

    /// <summary>
    /// True when CoinGlass's pre-calculated arbitrage screening currently lists this symbol
    /// as a hot cross-exchange opportunity above the configured APR threshold. Populated by
    /// SignalEngine when ICoinGlassScreeningProvider is available. Used as a priority hint
    /// during opportunity ranking and surfaced on the dashboard as a visual badge.
    /// </summary>
    public bool IsCoinGlassHot { get; set; }

    /// <summary>Long-leg rate projected to the reference interval (default 8h). Display/ranking only — never PnL.</summary>
    public decimal LongRateReferenceInterval { get; set; }

    /// <summary>Short-leg rate projected to the reference interval (default 8h). Display/ranking only.</summary>
    public decimal ShortRateReferenceInterval { get; set; }

    /// <summary>Spread between the two legs at the reference interval. Display/ranking only.</summary>
    public decimal SpreadReferenceInterval { get; set; }

    /// <summary>The reference interval in hours used for the *ReferenceInterval fields (typically 8).</summary>
    public int ReferenceIntervalHours { get; set; }
}
