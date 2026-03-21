namespace FundingRateArb.Application.DTOs;

public class OpportunityResultDto
{
    public List<ArbitrageOpportunityDto> Opportunities { get; set; } = [];
    public PipelineDiagnosticsDto Diagnostics { get; set; } = new();
}
