using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Web.ViewModels;

public class PassedOpportunitiesViewModel
{
    public List<OpportunitySnapshot> Snapshots { get; set; } = [];
    public int Days { get; set; } = 1;
    public int Skip { get; set; }
    public int Take { get; set; } = 100;
    public bool HasMore { get; set; }

    // Summary stats
    public int TotalOpportunitiesSeen { get; set; }
    public int TotalOpened { get; set; }
    public decimal OpenedPct { get; set; }
    public string TopSkipReason { get; set; } = "N/A";
    public List<SkipReasonStat> SkipReasons { get; set; } = [];
}

public class SkipReasonStat
{
    public string Reason { get; set; } = null!;
    public int Count { get; set; }
}
