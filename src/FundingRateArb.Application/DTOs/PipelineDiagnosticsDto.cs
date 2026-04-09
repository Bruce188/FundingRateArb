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

    public int PairsFilteredByBreakeven { get; set; }
    public int PairsPassing { get; set; }
    public decimal BestRawSpread { get; set; }
    public int StalenessMinutes { get; set; }
    public decimal MinVolumeThreshold { get; set; }
    public decimal OpenThreshold { get; set; }
}
