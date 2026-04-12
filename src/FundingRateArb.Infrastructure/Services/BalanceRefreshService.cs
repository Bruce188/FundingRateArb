using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.Services;

/// <summary>
/// Background service that pushes exchange balance updates every 60s
/// regardless of bot state. Uses per-tick scoping to avoid DbContext lifetime issues.
/// </summary>
public class BalanceRefreshService : BackgroundService
{
    private const int RefreshIntervalSeconds = 60;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BalanceRefreshService> _logger;

    public BalanceRefreshService(
        IServiceScopeFactory scopeFactory,
        ILogger<BalanceRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Initial delay to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshBalancesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Balance refresh cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(RefreshIntervalSeconds), ct);
        }
    }

    internal async Task RefreshBalancesAsync(CancellationToken ct)
    {
        List<string> userIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            userIds = await uow.UserConfigurations.GetAllEnabledUserIdsAsync();
        }

        await Parallel.ForEachAsync(userIds, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = ct,
        }, async (userId, token) =>
        {
            try
            {
                using var innerScope = _scopeFactory.CreateScope();
                var aggregator = innerScope.ServiceProvider.GetRequiredService<IBalanceAggregator>();
                var notifier = innerScope.ServiceProvider.GetRequiredService<ISignalRNotifier>();
                var snapshot = await aggregator.GetBalanceSnapshotAsync(userId, token);
                await notifier.PushBalanceUpdateAsync(userId, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to refresh balance for user {UserId}", userId);
            }
        });
    }
}
