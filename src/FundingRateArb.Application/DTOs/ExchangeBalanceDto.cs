namespace FundingRateArb.Application.DTOs;

public class ExchangeBalanceDto
{
    public int ExchangeId { get; set; }
    public string ExchangeName { get; set; } = null!;
    public decimal AvailableUsdc { get; set; }
    public DateTime FetchedAt { get; set; }
}

public class BalanceSnapshotDto
{
    public List<ExchangeBalanceDto> Balances { get; set; } = new();
    public decimal TotalAvailableUsdc { get; set; }
    public DateTime FetchedAt { get; set; }
}
