using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Web.ViewModels;

public class PositionDetailsViewModel
{
    public ArbitragePosition Position { get; set; } = null!;
    public decimal UnrealizedPnl { get; set; }
    public decimal DurationHours { get; set; }
}
