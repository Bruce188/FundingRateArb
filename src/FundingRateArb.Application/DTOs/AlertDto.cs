using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.DTOs;

public class AlertDto
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int? ArbitragePositionId { get; set; }
    public AlertType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public string Message { get; set; } = null!;
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}
