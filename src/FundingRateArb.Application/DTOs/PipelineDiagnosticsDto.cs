namespace FundingRateArb.Application.DTOs;

public class PipelineDiagnosticsDto
{
    public int TotalRatesLoaded { get; set; }
    public int RatesAfterStalenessFilter { get; set; }
    public int TotalPairsEvaluated { get; set; }
    public int PairsFilteredByVolume { get; set; }
    public int PairsFilteredByThreshold { get; set; }
    public int NetPositiveBelowThreshold { get; set; }

    /// <summary>
    /// Opportunities that passed OpenThreshold but were filtered by the MinEdgeMultiplier
    /// guardrail (net yield below 3x amortized entry cost by default). Tracked separately
    /// from NetPositiveBelowThreshold so operators can distinguish "widen the threshold"
    /// from "loosen the edge multiplier" when tuning the configuration.
    /// </summary>
    public int NetPositiveBelowEdgeGuardrail { get; set; }

    /// <summary>
    /// Opportunities filtered because net yield × MinHoldTimeHours would not cover
    /// MinEdgeMultiplier × totalEntryCost — the bot cannot realistically profit from
    /// these inside its worst-case hold window, regardless of position size. Distinct
    /// from NetPositiveBelowEdgeGuardrail, which uses the more forgiving
    /// FeeAmortizationHours as its denominator.
    /// </summary>
    public int PairsFilteredByBreakEvenSize { get; set; }

    public int PairsFilteredByBreakeven { get; set; }

    /// <summary>
    /// Opportunities filtered because, for at least one leg, the cached order-book
    /// snapshot reports BestBid=0 AND BestAsk=0 (empty book). Exchange-agnostic;
    /// one-sided emptiness (bid=0 OR ask=0 but not both) does not trigger this counter.
    /// </summary>
    public int PairsFilteredByEmptyBook { get; set; }

    /// <summary>
    /// Opportunities filtered because sized notional would exceed an exchange's
    /// per-symbol <c>MAX_NOTIONAL_VALUE</c> limit on at least one leg. Tracked so
    /// operators see "WLFI dropped by Aster cap" instead of a silent skip.
    /// </summary>
    public int PairsFilteredByExchangeSymbolCap { get; set; }

    /// <summary>
    /// Opportunities hard-rejected by the entry-side trend gate because the funding
    /// spread was not favorable for all <c>MinConsecutiveFavorableCycles</c> snapshots,
    /// or insufficient history was available to confirm the trend.
    /// </summary>
    public int PairsFilteredByTrendUnconfirmed { get; set; }

    /// <summary>
    /// Opportunities filtered because the (LongExchangeName, ShortExchangeName) directional pair is currently
    /// in the deny list (auto or manual). Consulted via <see cref="FundingRateArb.Application.Interfaces.IPairDenyListSnapshot"/>
    /// — the snapshot is refreshed per-cycle by <c>BotOrchestrator</c>, so changes take effect on the next cycle's
    /// <c>SignalEngine.ComputeAndCacheAsync</c> invocation.
    /// </summary>
    public int PairsFilteredByDenyList { get; set; }

    public int PairsPassing { get; set; }
    public decimal BestRawSpread { get; set; }
    public int StalenessMinutes { get; set; }
    public decimal MinVolumeThreshold { get; set; }
    public decimal OpenThreshold { get; set; }

    /// <summary>
    /// The capital amount (USDC) that SignalEngine actually evaluated against during this pipeline
    /// invocation — <c>min(liveExchangeBalance, config.TotalCapitalUsdc)</c> when
    /// <c>ICapitalProvider</c> is wired; <c>null</c> when SignalEngine ran without a provider
    /// (e.g. unit tests that omit the dependency).
    /// </summary>
    public decimal? EvaluatedCapitalUsdc { get; set; }
}
