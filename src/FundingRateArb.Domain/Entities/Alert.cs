using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class Alert
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public int? ArbitragePositionId { get; set; }
    public AlertType Type { get; set; }
    public AlertSeverity Severity { get; set; }

    [Required, MaxLength(1000)]
    public string Message { get; set; } = null!;

    public bool IsRead { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser User { get; set; } = null!;
    public ArbitragePosition? ArbitragePosition { get; set; }
}
