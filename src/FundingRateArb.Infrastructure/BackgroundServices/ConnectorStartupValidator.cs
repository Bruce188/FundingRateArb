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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConnectorStartupValidator> _logger;

    public ConnectorStartupValidator(
        IServiceScopeFactory scopeFactory,
        ILogger<ConnectorStartupValidator> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
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

                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
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
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
