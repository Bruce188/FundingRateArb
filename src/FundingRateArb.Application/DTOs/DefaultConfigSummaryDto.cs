using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.DTOs;

/// <summary>
/// A projection of BotConfiguration containing only user-visible default fields.
/// Used to display "(Default: X)" hints in user settings without exposing admin-only
/// fields (VolumeFraction, BreakevenHoursMax, UpdatedByUserId).
/// </summary>
public class DefaultConfigSummaryDto
{
    public decimal TotalCapitalUsdc { get; set; }
    public int DefaultLeverage { get; set; }
    public int MaxConcurrentPositions { get; set; }
    public decimal MaxCapitalPerPosition { get; set; }
    public decimal OpenThreshold { get; set; }
    public decimal CloseThreshold { get; set; }
    public decimal AlertThreshold { get; set; }
    public decimal StopLossPct { get; set; }
    public int MaxHoldTimeHours { get; set; }
    public decimal DailyDrawdownPausePct { get; set; }
    public int ConsecutiveLossPause { get; set; }
    public decimal MaxExposurePerAsset { get; set; }
    public decimal MaxExposurePerExchange { get; set; }
    public AllocationStrategy AllocationStrategy { get; set; }
    public int AllocationTopN { get; set; }
    public decimal FeeAmortizationHours { get; set; }
    public decimal MinPositionSizeUsdc { get; set; }
    public decimal MinVolume24hUsdc { get; set; }
    public int RateStalenessMinutes { get; set; }
    public int FundingWindowMinutes { get; set; }
}
