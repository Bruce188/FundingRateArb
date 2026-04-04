namespace FundingRateArb.Application.DTOs;

public class MarginStateDto
{
    public decimal MarginUsed { get; set; }
    public decimal MarginAvailable { get; set; }
    public decimal? LiquidationPrice { get; set; }
    public decimal MarginUtilizationPct { get; set; }
}
