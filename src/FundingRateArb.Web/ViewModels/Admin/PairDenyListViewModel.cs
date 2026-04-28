using System;
using System.Collections.Generic;

namespace FundingRateArb.Web.ViewModels.Admin;

public class PairDenyListViewModel
{
    public List<PairDenyListRow> Rows { get; set; } = new();
    public DateTime SnapshotAt { get; set; }
    public int DeniedCount { get; set; }
    public bool AutoDenyEnabled { get; set; }
}

public class PairDenyListRow
{
    public string LongExchangeName { get; set; } = string.Empty;
    public string ShortExchangeName { get; set; } = string.Empty;
    public int CloseCount { get; set; }
    public int WinCount { get; set; }
    public decimal TotalPnlUsdc { get; set; }
    public int AvgHoldSec { get; set; }
    public bool IsDenied { get; set; }
    public DateTime? DeniedUntil { get; set; }
    public string? DeniedReason { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}
