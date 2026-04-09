namespace FundingRateArb.Application.DTOs;

public class OpportunityResultDto
{
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
    public List<ArbitrageOpportunityDto> AllNetPositive { get; set; } = [];
    public PipelineDiagnosticsDto? Diagnostics { get; set; } = new();
    public List<CircuitBreakerStatusDto> CircuitBreakers { get; set; } = [];

    // Degraded-state signalling. Defaults represent the healthy success path so
    // existing call sites and tests do not need to opt in — only the SignalEngine
    // catch block flips these fields when the data source is unavailable.
    public bool DatabaseAvailable { get; set; } = true;
    public bool IsSuccess { get; set; } = true;
    public SignalEngineFailureReason FailureReason { get; set; } = SignalEngineFailureReason.None;
    public string? Error { get; set; }
}

public enum SignalEngineFailureReason
{
    None = 0,
    DatabaseUnavailable = 1,
}
