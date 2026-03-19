using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class PositionDetailsViewModel
{
    public PositionDetailsDto Position { get; set; } = null!;
    public decimal UnrealizedPnl { get; set; }
    public decimal DurationHours { get; set; }
}
