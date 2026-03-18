namespace FundingRateArb.Application.DTOs;

public class FundingRateDto
{
    public string ExchangeName { get; set; } = null!;
    public string Symbol { get; set; } = null!;
    public decimal RatePerHour { get; set; }
    public decimal RawRate { get; set; }
    public decimal MarkPrice { get; set; }
    public decimal IndexPrice { get; set; }
    public decimal Volume24hUsd { get; set; }
}
