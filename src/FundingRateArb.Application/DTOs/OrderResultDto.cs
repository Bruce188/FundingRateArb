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

    /// <summary>
    /// True when the order was rejected by a pre-flight guard (e.g. insufficient book depth)
    /// before any on-chain transaction was submitted. False for real on-chain reverts.
    /// </summary>
    public bool IsPreFlightRejection { get; set; }
}
