using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Web.ViewModels;

public class PositionAnalyticsIndexViewModel
{
    public List<PositionAnalyticsSummaryDto> Summaries { get; set; } = [];
    public int Skip { get; set; }
    public int Take { get; set; } = 50;
    public bool HasMore { get; set; }
}
