using System.ComponentModel.DataAnnotations;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Domain.Entities;

public class BotConfiguration
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; } = false;
    public decimal OpenThreshold { get; set; } = 0.0003m;
    public decimal AlertThreshold { get; set; } = 0.0001m;
    public decimal CloseThreshold { get; set; } = -0.00005m;

    [Range(0.01, 1.0)]
    public decimal StopLossPct { get; set; } = 0.15m;

    public int MaxHoldTimeHours { get; set; } = 72;
    public decimal VolumeFraction { get; set; } = 0.001m;

    [Range(0.01, 1.0)]
    public decimal MaxCapitalPerPosition { get; set; } = 0.80m;

    [Range(1, 168)]
    public int BreakevenHoursMax { get; set; } = 6;

    [Range(0.01, (double)decimal.MaxValue)]
    public decimal TotalCapitalUsdc { get; set; } = 107m;

    [Range(1, 125)]
    public int DefaultLeverage { get; set; } = 5;

    [Range(1, 100)]
    public int MaxConcurrentPositions { get; set; } = 1;

    public AllocationStrategy AllocationStrategy { get; set; } = AllocationStrategy.Concentrated;

    [Range(1, 20)]
    public int AllocationTopN { get; set; } = 3;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedByUserId { get; set; } = null!;
}
