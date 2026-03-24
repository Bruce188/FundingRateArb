using FundingRateArb.Application.Hubs;
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
}
