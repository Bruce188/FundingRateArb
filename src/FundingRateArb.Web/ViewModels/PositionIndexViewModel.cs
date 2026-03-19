using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Web.ViewModels;

public class PositionIndexViewModel
{
    public List<ArbitragePosition> Positions { get; set; } = [];
}
