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

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When an admin performs an action on behalf of another user (e.g., closing a position),
    /// this field records the admin's user ID for audit traceability.
    /// Null when the action was performed by the owning user or the system.
    /// </summary>
    [MaxLength(450)]
    public string? ActingUserId { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public ArbitragePosition? ArbitragePosition { get; set; }
}
