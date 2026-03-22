using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

/// <summary>
/// Runs once daily at 00:05 UTC. Sends a PnL summary email to each user
/// who has EmailDailySummary enabled.
/// </summary>
public class DailySummaryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(IServiceScopeFactory scopeFactory, ILogger<DailySummaryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            // Next run at 00:05 UTC tomorrow (or today if we haven't passed it yet)
            var nextRun = now.Date.AddMinutes(5);
            if (now >= nextRun)
                nextRun = nextRun.AddDays(1);

            var delay = nextRun - now;
            _logger.LogDebug("DailySummaryService next run in {Delay}", delay);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await SendDailySummariesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailySummaryService failed");
            }
        }
    }

    internal async Task SendDailySummariesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var signalEngine = scope.ServiceProvider.GetRequiredService<ISignalEngine>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var enabledUserIds = await uow.UserConfigurations.GetAllEnabledUserIdsAsync();

        // Hoist opportunity query before the loop — opportunities are global, not per-user
        var opportunityResult = await signalEngine.GetOpportunitiesWithDiagnosticsAsync(ct);
        var bestOpp = opportunityResult.Opportunities.OrderByDescending(o => o.SpreadPerHour).FirstOrDefault();

        foreach (var userId in enabledUserIds)
        {
            try
            {
                var userConfig = await uow.UserConfigurations.GetByUserAsync(userId);
                if (userConfig is null || !userConfig.EmailNotificationsEnabled || !userConfig.EmailDailySummary)
                    continue;

                var user = await userManager.FindByIdAsync(userId);
                if (user?.Email is null) continue;

                var openPositions = await uow.Positions.GetOpenByUserAsync(userId);
                var closedToday = (await uow.Positions.GetClosedSinceAsync(DateTime.UtcNow.Date))
                    .Where(p => p.UserId == userId)
                    .ToList();

                var unreadAlerts = await uow.Alerts.GetByUserAsync(userId, unreadOnly: true);

                var summary = new DailySummaryDto
                {
                    OpenPositionCount = openPositions.Count,
                    TotalPnl = openPositions.Sum(p => p.AccumulatedFunding),
                    ClosedTodayCount = closedToday.Count,
                    RealizedPnlToday = closedToday.Sum(p => p.RealizedPnl ?? 0m),
                    AlertsCount = unreadAlerts.Count,
                    BestAvailableSpread = bestOpp?.SpreadPerHour ?? 0m,
                    BestOpportunityAsset = bestOpp?.AssetSymbol,
                };

                await emailService.SendDailySummaryAsync(user.Email, summary, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send daily summary for user {UserId}", userId);
            }
        }
    }
}
