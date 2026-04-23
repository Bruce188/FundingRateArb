using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

/// <summary>
/// On startup (after a 30-second warm-up) and every 30 minutes thereafter,
/// validates dYdX credentials for all active users and notifies on failure.
/// </summary>
public class ConnectorStartupValidator : BackgroundService
{
    internal static readonly TimeSpan IterationInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan WarmUpDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan PerUserThrottle = TimeSpan.FromMilliseconds(500);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConnectorStartupValidator> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public ConnectorStartupValidator(
        IServiceScopeFactory scopeFactory,
        ILogger<ConnectorStartupValidator> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _delayAsync(WarmUpDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<IUserSettingsService>();
                var factory = scope.ServiceProvider.GetRequiredService<IExchangeConnectorFactory>();
                var notifier = scope.ServiceProvider.GetRequiredService<ISignalRNotifier>();

                var userIds = await users.GetUsersWithCredentialsAsync("dYdX", stoppingToken);

                _logger.LogDebug("ConnectorStartupValidator: validating dYdX credentials for {Count} user(s)", userIds.Count);

                foreach (var userId in userIds)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var r = await factory.ValidateDydxAsync(userId, stoppingToken);
                    if (r.Reason != DydxCredentialFailureReason.None)
                    {
                        _logger.LogWarning(
                            "ConnectorStartupValidator: dYdX credential invalid for user {UserId} — {Reason}: {Field}",
                            userId, r.Reason, r.MissingField);

                        await notifier.PushNotificationAsync(
                            userId,
                            $"dYdX credentials invalid — {r.Reason} ({r.MissingField})");
                    }

                    await _delayAsync(PerUserThrottle, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConnectorStartupValidator iteration failed");
            }

            try
            {
                await _delayAsync(IterationInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
