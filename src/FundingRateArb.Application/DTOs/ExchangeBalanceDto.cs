namespace FundingRateArb.Application.DTOs;

public class ExchangeBalanceDto
{
    public int ExchangeId { get; set; }
    public string ExchangeName { get; set; } = null!;
    public decimal AvailableUsdc { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime FetchedAt { get; set; }
    public bool IsStale { get; set; }
    public bool IsUnavailable { get; set; }
    public decimal? LastKnownAvailableUsdc { get; set; }
    public DateTimeOffset? LastKnownAt { get; set; }

    public bool IsFallbackEligible =>
        IsStale &&
        LastKnownAvailableUsdc.HasValue &&
        LastKnownAt.HasValue &&
        (FetchedAt - LastKnownAt.Value.UtcDateTime) <= TimeSpan.FromMinutes(5);
}

public class BalanceSnapshotDto
{
    public List<ExchangeBalanceDto> Balances { get; set; } = new();
    public decimal TotalAvailableUsdc { get; set; }
    public DateTime FetchedAt { get; set; }
}
