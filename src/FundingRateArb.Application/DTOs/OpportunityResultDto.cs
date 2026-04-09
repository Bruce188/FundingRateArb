namespace FundingRateArb.Application.DTOs;

public class OpportunityResultDto
{
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
    public List<ArbitrageOpportunityDto> AllNetPositive { get; set; } = [];
    public PipelineDiagnosticsDto? Diagnostics { get; set; } = new();
    public List<CircuitBreakerStatusDto> CircuitBreakers { get; set; } = [];

    // plan-v60 Task 3.2: degraded-state signalling for the dashboard banner.
    // Defaults represent the healthy success path so that existing call sites
    // and tests do not need to opt in — only the SignalEngine catch block flips
    // these fields when the data source is unavailable.
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
