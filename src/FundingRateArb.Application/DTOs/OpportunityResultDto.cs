namespace FundingRateArb.Application.DTOs;

public class OpportunityResultDto
{
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
    public List<ArbitrageOpportunityDto> AllNetPositive { get; set; } = [];
    public PipelineDiagnosticsDto? Diagnostics { get; set; } = new();
    public List<CircuitBreakerStatusDto> CircuitBreakers { get; set; } = [];
}
