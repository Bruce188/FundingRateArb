using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class AlertIndexViewModel
{
    public List<AlertDto> Alerts { get; set; } = [];
    public int UnreadCount { get; set; }
}
