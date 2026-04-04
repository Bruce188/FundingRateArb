namespace FundingRateArb.Application.DTOs;

public class PipelineDiagnosticsDto
{
    public int TotalRatesLoaded { get; set; }
    public int RatesAfterStalenessFilter { get; set; }
    public int TotalPairsEvaluated { get; set; }
    public int PairsFilteredByVolume { get; set; }
    public int PairsFilteredByThreshold { get; set; }
    public int NetPositiveBelowThreshold { get; set; }
    public int PairsFilteredByBreakeven { get; set; }
    public int PairsPassing { get; set; }
    public decimal BestRawSpread { get; set; }
    public int StalenessMinutes { get; set; }
    public decimal MinVolumeThreshold { get; set; }
    public decimal OpenThreshold { get; set; }
}
