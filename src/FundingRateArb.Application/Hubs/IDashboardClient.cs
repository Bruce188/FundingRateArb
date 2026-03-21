using FundingRateArb.Application.DTOs;

namespace FundingRateArb.Application.Hubs;

/// <summary>
/// Strongly-typed SignalR hub client interface.
/// Full implementation of DashboardHub lives in the Web project (Phase 8).
/// </summary>
public interface IDashboardClient
{
    Task ReceiveDashboardUpdate(DashboardDto data);
    Task ReceiveFundingRateUpdate(List<FundingRateDto> rates);
    Task ReceivePositionUpdate(PositionSummaryDto position);
    Task ReceiveNotification(string message);
    Task ReceiveAlert(AlertDto alert);
    Task ReceiveOpportunityUpdate(OpportunityResultDto result);
    Task ReceiveStatusExplanation(string message, string severity);
}
