using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class UserConfiguration
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;
    public bool IsEnabled { get; set; }
    public decimal OpenThreshold { get; set; } = 0.0002m;
    public decimal CloseThreshold { get; set; } = 0.00005m;
    public decimal AlertThreshold { get; set; } = 0.00015m;
    public int DefaultLeverage { get; set; } = 5;

    /// <summary>
    /// Optional per-user hard ceiling on effective leverage. When set, overrides the global
    /// BotConfiguration.MaxLeverageCap for this user — but only downward (users can tighten
    /// their own cap below global, never raise above global). Null means fall back to the
    /// global cap. Enforced at order placement in ExecutionEngine.
    /// </summary>
    [Range(1, 50)]
    public int? MaxLeverageCap { get; set; }

    public decimal TotalCapitalUsdc { get; set; } = 39m;
    public decimal MaxCapitalPerPosition { get; set; } = 0.90m;
    public int MaxConcurrentPositions { get; set; } = 1;
    public decimal StopLossPct { get; set; } = 0.10m;
    public int MaxHoldTimeHours { get; set; } = 48;
    public AllocationStrategy AllocationStrategy { get; set; } = AllocationStrategy.Concentrated;
    public int AllocationTopN { get; set; } = 3;
    public decimal FeeAmortizationHours { get; set; } = 12m;
    public decimal MinPositionSizeUsdc { get; set; } = 5m;
    public decimal MinVolume24hUsdc { get; set; } = 50000m;
    public int RateStalenessMinutes { get; set; } = 15;
    public decimal DailyDrawdownPausePct { get; set; } = 0.08m;
    public int ConsecutiveLossPause { get; set; } = 3;
    public int FundingWindowMinutes { get; set; } = 10;
    public decimal MaxExposurePerAsset { get; set; } = 0.5m;
    public decimal MaxExposurePerExchange { get; set; } = 0.7m;
    /// <summary>Enable dry-run (paper trading) mode for this user.</summary>
    public bool DryRunEnabled { get; set; }

    /// <summary>Minimum spread/hr improvement over current position to justify rotation.</summary>
    [Range(0, 0.01)]
    public decimal RotationThresholdPerHour { get; set; } = 0.0003m;

    /// <summary>Minimum minutes a position must be held before rotation is considered.</summary>
    [Range(0, 1440)]
    public int MinHoldBeforeRotationMinutes { get; set; } = 30;

    /// <summary>Maximum position rotations per day to prevent churning.</summary>
    [Range(0, 50)]
    public int MaxRotationsPerDay { get; set; } = 5;

    public bool EmailNotificationsEnabled { get; set; }
    public bool EmailCriticalAlerts { get; set; }
    public bool EmailDailySummary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUpdatedAt { get; set; }

    // Navigation
    public ApplicationUser User { get; set; } = null!;
}
