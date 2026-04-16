using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.DTOs;

public class OrderResultDto
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string? Error { get; set; }
    public decimal FilledPrice { get; set; }
    public decimal FilledQuantity { get; set; }
    public bool IsEstimatedFill { get; set; }
    public LighterOrderRevertReason RevertReason { get; set; }
}
