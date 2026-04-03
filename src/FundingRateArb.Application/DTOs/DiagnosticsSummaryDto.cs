namespace FundingRateArb.Application.DTOs;

public class DiagnosticsSummaryDto
{
    public DateTime GeneratedAt { get; set; }
    public Dictionary<string, int> PositionStatusCounts { get; set; } = new();
    public Dictionary<string, int> AlertSeverityCounts { get; set; } = new();
    public List<CircuitBreakerStatusDto> CircuitBreakers { get; set; } = new();
    public List<RecentErrorDto> RecentErrors { get; set; } = new();
    public List<RecentCloseDto> RecentCloses { get; set; } = new();
    public List<FundingRateFreshnessDto> FundingRateFreshness { get; set; } = new();
}

public class RecentErrorDto
{
    public string Message { get; set; } = null!;
    public string Severity { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public int Count { get; set; } = 1;
}

public class RecentCloseDto
{
    public int PositionId { get; set; }
    public string Asset { get; set; } = null!;
    public string CloseReason { get; set; } = null!;
    public decimal? PnlUsdc { get; set; }
    public DateTime ClosedAt { get; set; }
}

public class FundingRateFreshnessDto
{
    public string ExchangeName { get; set; } = null!;
    public DateTime LastRecordedAt { get; set; }
    public int StaleMinutes { get; set; }
}
