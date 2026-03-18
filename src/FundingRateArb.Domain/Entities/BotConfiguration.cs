namespace FundingRateArb.Domain.Entities;

public class BotConfiguration
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; } = false;
    public decimal OpenThreshold { get; set; } = 0.0003m;
    public decimal AlertThreshold { get; set; } = 0.0001m;
    public decimal CloseThreshold { get; set; } = -0.00005m;
    public decimal StopLossPct { get; set; } = 0.15m;
    public int MaxHoldTimeHours { get; set; } = 72;
    public decimal VolumeFraction { get; set; } = 0.001m;
    public decimal MaxCapitalPerPosition { get; set; } = 0.80m;
    public int BreakevenHoursMax { get; set; } = 6;
    public decimal TotalCapitalUsdc { get; set; } = 107m;
    public int DefaultLeverage { get; set; } = 5;
    public int MaxConcurrentPositions { get; set; } = 1;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedByUserId { get; set; } = null!;
}
