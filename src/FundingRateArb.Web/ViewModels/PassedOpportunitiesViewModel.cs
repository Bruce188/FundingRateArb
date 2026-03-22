using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Web.ViewModels;

public class PassedOpportunitiesViewModel
{
    public List<OpportunitySnapshot> Snapshots { get; set; } = [];
    public int Days { get; set; } = 1;
    public int Skip { get; set; }
    public int Take { get; set; } = 100;
    public bool HasMore { get; set; }
}
