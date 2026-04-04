using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Interfaces;

/// <summary>
/// Abstracts all SignalR hub communication for the bot orchestrator.
/// Implementation lives in Infrastructure; interface in Application to avoid SignalR dependency.
/// </summary>
public interface ISignalRNotifier
{
    Task PushOpportunityUpdateAsync(OpportunityResultDto result);

    Task PushDashboardUpdateAsync(
        List<ArbitragePosition> openPositions,
        List<ArbitrageOpportunityDto> opportunities,
        bool botEnabled,
        int openingCount,
        int needsAttentionCount);

    Task PushPositionUpdatesAsync(List<ArbitragePosition> openPositions, BotConfiguration config);

    Task PushPositionRemovalsAsync(
        IReadOnlyList<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)> reapedPositions,
        List<(int PositionId, string UserId)> closedPositions);

    Task PushRebalanceRemovalsAsync(List<(int Id, string UserId)> removals);

    Task PushNewAlertsAsync(IUnitOfWork uow);

    Task PushStatusExplanationAsync(string? userId, string message, string severity);

    Task PushBalanceUpdateAsync(string userId, BalanceSnapshotDto snapshot);

    Task PushNotificationAsync(string userId, string message);
}
