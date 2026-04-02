namespace FundingRateArb.Application.DTOs;

public class CircuitBreakerStatusDto
{
    public string ExchangeName { get; set; } = null!;
    public int ExchangeId { get; set; }
    public DateTime BrokenUntil { get; set; }
    public int RemainingMinutes { get; set; }
}
