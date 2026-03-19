using FundingRateArb.Domain.Entities;

namespace FundingRateArb.Web.ViewModels;

public class AlertIndexViewModel
{
    public List<Alert> Alerts { get; set; } = [];
    public int UnreadCount { get; set; }
}
