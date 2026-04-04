using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FundingRateArb.Infrastructure.Hubs;

/// <summary>
/// Canonical group name constants for SignalR hub groups.
/// Used by DashboardHub, FundingRateFetcher, BotOrchestrator, etc.
/// </summary>
public static class HubGroups
{
    public const string MarketData = "MarketData";
    public const string Admins = "Admins";
}

/// <summary>
/// Strongly-typed SignalR hub.
/// Lives in Infrastructure (not Web) so background services can inject IHubContext
/// without creating a circular dependency back to the Web project.
/// </summary>
[Authorize]
public class DashboardHub : Hub<IDashboardClient>
{
    private readonly IUnitOfWork _uow;

    public DashboardHub(IUnitOfWork uow)
    {
        _uow = uow;
    }

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.MarketData);

        if (Context.User?.IsInRole("Admin") == true)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Admins);
        }

        var userId = Context.UserIdentifier;
        if (userId != null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
        }

        await base.OnConnectedAsync();
    }

    public async Task RejoinGroups()
    {
        var userId = Context.UserIdentifier;
        if (userId == null)
            throw new HubException("Authentication required");

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.MarketData);

        if (Context.User?.IsInRole("Admin") == true)
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Admins);

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{userId}");
    }

    public async Task RequestFullUpdate()
    {
        var userId = Context.UserIdentifier;
        if (userId == null)
            throw new HubException("Authentication required");

        var isAdmin = Context.User?.IsInRole("Admin") == true;
        var openPositions = isAdmin
            ? await _uow.Positions.GetOpenAsync()
            : await _uow.Positions.GetOpenByUserAsync(userId);
        var botConfig = await _uow.BotConfig.GetActiveAsync();
        var openingCount = await _uow.Positions.CountByStatusAsync(PositionStatus.Opening);
        var needsAttentionCount = await _uow.Positions.CountByStatusesAsync(
            PositionStatus.EmergencyClosed, PositionStatus.Failed);
        var totalPnl = openPositions.Sum(p => p.AccumulatedFunding);
        var bestSpread = openPositions.Count > 0
            ? openPositions.Max(p => p.CurrentSpreadPerHour)
            : 0m;

        var dto = new DashboardDto
        {
            BotEnabled = botConfig?.IsEnabled ?? false,
            OpenPositionCount = openPositions.Count,
            OpeningPositionCount = openingCount,
            NeedsAttentionCount = needsAttentionCount,
            TotalPnl = totalPnl,
            BestSpread = bestSpread,
        };

        await Clients.Caller.ReceiveDashboardUpdate(dto);
    }
}
