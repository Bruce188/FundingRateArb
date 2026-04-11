using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
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
        BotOperatingState operatingState,
        int openingCount,
        int needsAttentionCount,
        OpportunityResultDto? opportunityResult = null);

    Task PushPositionUpdatesAsync(
        List<ArbitragePosition> openPositions,
        BotConfiguration config,
        IReadOnlyDictionary<int, ComputedPositionPnl>? computedPnl = null);

    Task PushPositionRemovalsAsync(
        IReadOnlyList<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)> reapedPositions,
        List<(int PositionId, string UserId)> closedPositions);

    Task PushRebalanceRemovalsAsync(List<(int Id, string UserId)> removals);

    /// <summary>
    /// Pushes new unread alerts to each user's SignalR group.
    /// Uses a rolling time window and deduplication set to avoid replaying alerts.
    /// Not thread-safe — must only be called from the bot cycle (which holds _cycleLock).
    /// </summary>
    Task PushNewAlertsAsync(IUnitOfWork uow);

    Task PushStatusExplanationAsync(string? userId, string message, string severity);

    Task PushBalanceUpdateAsync(string userId, BalanceSnapshotDto snapshot);

    Task PushNotificationAsync(string userId, string message);
}
