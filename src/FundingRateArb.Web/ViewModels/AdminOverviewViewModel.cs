namespace FundingRateArb.Web.ViewModels;

public class AdminOverviewViewModel
{
    public int TotalActiveUsers { get; set; }
    public int TotalOpenPositions { get; set; }
    public decimal AggregateRealizedPnl { get; set; }
    public decimal AggregateUnrealizedPnl { get; set; }
    public bool GlobalBotEnabled { get; set; }
    public List<UserSummaryItem> Users { get; set; } = [];
}

public class UserSummaryItem
{
    public string UserId { get; set; } = null!;
    public string Email { get; set; } = null!;
    public bool BotEnabled { get; set; }
    public int OpenPositions { get; set; }
    public decimal RealizedPnl { get; set; }
    public int ConfiguredExchanges { get; set; }
    public DateTime? LastActivity { get; set; }
}
