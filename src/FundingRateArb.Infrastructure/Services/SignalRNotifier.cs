using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Extensions;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

public class SignalRNotifier : ISignalRNotifier
{
    private readonly IHubContext<DashboardHub, IDashboardClient> _hubContext;
    private readonly ILogger<SignalRNotifier> _logger;

    // H6: Track pushed alert IDs to prevent duplicate pushes across cycles
    private readonly ConcurrentDictionary<int, byte> _pushedAlertIds = new();

    // M7: Track when alerts were last pushed so each cycle uses only the elapsed window (no replays)
    private DateTime _lastAlertPushUtc = DateTime.UtcNow.AddMinutes(-5);

    /// <summary>Exposes pushed alert IDs for unit testing.</summary>
    internal ConcurrentDictionary<int, byte> PushedAlertIds => _pushedAlertIds;

    public SignalRNotifier(
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        ILogger<SignalRNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task PushOpportunityUpdateAsync(OpportunityResultDto result)
    {
        try
        {
            await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveOpportunityUpdate(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push opportunity update via SignalR");
        }
    }

    public async Task PushDashboardUpdateAsync(
        List<ArbitragePosition> openPositions,
        List<ArbitrageOpportunityDto> opportunities,
        bool botEnabled,
        int openingCount,
        int needsAttentionCount)
    {
        try
        {
            var totalPnl = openPositions.Sum(p => p.AccumulatedFunding);
            var bestSpread = opportunities.Count > 0
                ? opportunities.Max(o => o.SpreadPerHour)
                : 0m;

            var dto = new DashboardDto
            {
                BotEnabled = botEnabled,
                OpenPositionCount = openPositions.Count,
                OpeningPositionCount = openingCount,
                NeedsAttentionCount = needsAttentionCount,
                TotalPnl = totalPnl,
                BestSpread = bestSpread,
            };

            await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveDashboardUpdate(dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push dashboard update via SignalR");
        }
    }

    public async Task PushPositionUpdatesAsync(List<ArbitragePosition> openPositions, BotConfiguration config)
    {
        var tasks = openPositions.Select(async pos =>
        {
            try
            {
                var dto = MapPositionToDto(pos, config);
                await _hubContext.Clients.Group($"user-{pos.UserId}").ReceivePositionUpdate(dto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push position update for #{PositionId}", pos.Id);
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task PushPositionRemovalsAsync(
        IReadOnlyList<(int PositionId, string UserId, int LongExchangeId, int ShortExchangeId, PositionStatus OriginalStatus)> reapedPositions,
        List<(int PositionId, string UserId)> closedPositions)
    {
        var allRemovals = reapedPositions
            .Select(r => (r.PositionId, r.UserId))
            .Concat(closedPositions);
        var tasks = allRemovals.Select(async removal =>
        {
            try
            {
                await _hubContext.Clients.Group($"user-{removal.UserId}").ReceivePositionRemoval(removal.PositionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push position removal for #{PositionId}", removal.PositionId);
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task PushRebalanceRemovalsAsync(List<(int Id, string UserId)> removals)
    {
        var tasks = removals.Select(async r =>
        {
            try
            {
                await _hubContext.Clients.Group($"user-{r.UserId}").ReceivePositionRemoval(r.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push rebalance removal for #{PositionId}", r.Id);
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task PushNewAlertsAsync(IUnitOfWork uow)
    {
        try
        {
            var since = _lastAlertPushUtc;
            var now = DateTime.UtcNow;
            var window = now - since;

            var recentAlerts = await uow.Alerts.GetRecentUnreadAsync(window);
            _lastAlertPushUtc = now;

            // H6: Prune old IDs periodically to prevent unbounded growth
            if (_pushedAlertIds.Count > 1000)
            {
                _pushedAlertIds.Clear();
            }

            foreach (var alert in recentAlerts)
            {
                // H6: Skip already-pushed alerts to prevent duplicate pushes
                if (!_pushedAlertIds.TryAdd(alert.Id, 0))
                {
                    continue;
                }

                var dto = new AlertDto
                {
                    Id = alert.Id,
                    UserId = alert.UserId,
                    ArbitragePositionId = alert.ArbitragePositionId,
                    Type = alert.Type,
                    Severity = alert.Severity,
                    Message = alert.Message,
                    IsRead = alert.IsRead,
                    CreatedAt = alert.CreatedAt,
                };

                await _hubContext.Clients.Group($"user-{alert.UserId}").ReceiveAlert(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push alert updates via SignalR");
        }
    }

    public async Task PushStatusExplanationAsync(string? userId, string message, string severity)
    {
        try
        {
            if (userId is not null)
            {
                await _hubContext.Clients.Group($"user-{userId}").ReceiveStatusExplanation(message, severity);
            }
            else
            {
                await _hubContext.Clients.Group(HubGroups.MarketData).ReceiveStatusExplanation(message, severity);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push status explanation via SignalR");
        }
    }

    public async Task PushBalanceUpdateAsync(string userId, BalanceSnapshotDto snapshot)
    {
        try
        {
            await _hubContext.Clients.Group($"user-{userId}").ReceiveBalanceUpdate(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push balance update for user {UserId}", userId);
        }
    }

    public async Task PushNotificationAsync(string userId, string message)
    {
        try
        {
            await _hubContext.Clients.Group($"user-{userId}").ReceiveNotification(message);
            await _hubContext.Clients.Group(HubGroups.Admins).ReceiveNotification(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to push notification for user {UserId}", userId);
        }
    }

    internal static PositionSummaryDto MapPositionToDto(ArbitragePosition pos, BotConfiguration config)
    {
        var dto = pos.ToSummaryDto();
        ComputeWarnings(dto, pos, config);
        return dto;
    }

    /// <summary>
    /// Evaluates warning conditions for a position and populates WarningLevel + WarningTypes.
    /// Conditions checked:
    ///   - SpreadRisk: spread approaching or below close/alert thresholds
    ///   - TimeBased:  position approaching MaxHoldTimeHours
    ///   - Loss:       unrealized loss approaching StopLossPct
    /// The highest WarningLevel across all conditions is used.
    /// </summary>
    internal static void ComputeWarnings(PositionSummaryDto dto, ArbitragePosition pos, BotConfiguration config)
    {
        var warningLevel = WarningLevel.None;
        var warningTypes = new List<WarningType>();

        // SpreadRisk warnings
        if (pos.CurrentSpreadPerHour <= config.CloseThreshold)
        {
            warningTypes.Add(WarningType.SpreadRisk);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
        }
        else if (pos.CurrentSpreadPerHour <= config.AlertThreshold)
        {
            warningTypes.Add(WarningType.SpreadRisk);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
        }

        // TimeBased warnings
        var hoursOpen = (DateTime.UtcNow - pos.OpenedAt).TotalHours;
        if (hoursOpen > config.MaxHoldTimeHours * 0.95)
        {
            warningTypes.Add(WarningType.TimeBased);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
        }
        else if (hoursOpen > config.MaxHoldTimeHours * 0.8)
        {
            warningTypes.Add(WarningType.TimeBased);
            warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
        }

        // Loss warnings (unrealized loss relative to margin and stop-loss)
        var unrealizedLoss = -pos.AccumulatedFunding; // positive means position is losing
        var stopLossAmount = config.StopLossPct * pos.MarginUsdc;
        if (unrealizedLoss > 0 && stopLossAmount > 0)
        {
            if (unrealizedLoss > stopLossAmount * 0.9m)
            {
                warningTypes.Add(WarningType.Loss);
                warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
            }
            else if (unrealizedLoss > stopLossAmount * 0.7m)
            {
                warningTypes.Add(WarningType.Loss);
                warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
            }
        }

        // PnlProgress warnings (when adaptive hold is enabled)
        if (config.AdaptiveHoldEnabled && pos.AccumulatedFunding > 0)
        {
            var entryFee = pos.EntryFeesUsdc > 0
                ? pos.EntryFeesUsdc
                : pos.SizeUsdc * pos.Leverage * 2m * PositionHealthMonitor.GetTakerFeeRate(
                    pos.LongExchange?.Name, pos.ShortExchange?.Name,
                    pos.LongExchange?.TakerFeeRate, pos.ShortExchange?.TakerFeeRate);
            if (entryFee > 0 && config.TargetPnlMultiplier > 0)
            {
                var pnlProgress = pos.AccumulatedFunding / (config.TargetPnlMultiplier * entryFee);
                if (pnlProgress > 0.9m)
                {
                    warningTypes.Add(WarningType.PnlProgress);
                    warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Critical);
                }
                else if (pnlProgress > 0.7m)
                {
                    warningTypes.Add(WarningType.PnlProgress);
                    warningLevel = (WarningLevel)Math.Max((int)warningLevel, (int)WarningLevel.Warning);
                }
            }
        }

        dto.WarningLevel = warningLevel;
        dto.WarningTypes = warningTypes;
    }
}
