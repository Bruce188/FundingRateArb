using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class ExchangeReconciliationHostedService : BackgroundService
{
    private const int DefaultIntervalMinutes = 5;
    private const string AdminEmail = "admin@fundingratearb.com";
    private static readonly TimeSpan AlertDedupWindow = TimeSpan.FromHours(4);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFundingRateReadinessSignal _readinessSignal;
    private readonly ILogger<ExchangeReconciliationHostedService> _logger;

    public ExchangeReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        IFundingRateReadinessSignal readinessSignal,
        ILogger<ExchangeReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _readinessSignal = readinessSignal;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _readinessSignal.WaitForReadyAsync(ct);

        // First tick: run immediately so the dashboard has data on startup; mirror FundingRateFetcher.
        await SafeRunOneTickAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            var intervalMinutes = await ReadIntervalMinutesAsync(ct);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), ct);
            }
            catch (OperationCanceledException) { break; }

            await SafeRunOneTickAsync(ct);
        }
    }

    private async Task<int> ReadIntervalMinutesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var config = await uow.BotConfig.GetActiveAsync();
            var minutes = config.ReconciliationIntervalMinutes;
            return minutes < 1 || minutes > 60 ? DefaultIntervalMinutes : minutes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read ReconciliationIntervalMinutes from BotConfiguration; using default");
            return DefaultIntervalMinutes;
        }
    }

    private async Task SafeRunOneTickAsync(CancellationToken ct)
    {
        try
        {
            await RunOneTickAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExchangeReconciliation tick threw — recording and continuing");
        }
    }

    private async Task RunOneTickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IExchangeReconciliationService>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var result = await service.RunReconciliationAsync(ct);
        uow.ReconciliationReports.Add(result.Report);

        var adminUserId = await ResolveAdminUserIdAsync(scope, ct);
        if (adminUserId is null)
        {
            _logger.LogWarning("No admin user found for reconciliation alert ownership; skipping alert add");
        }
        else
        {
            foreach (var description in result.AnomalyDescriptions)
            {
                var recent = await uow.Alerts.GetRecentAsync(
                    adminUserId, positionId: null, AlertType.OperationalWarning, AlertDedupWindow);
                if (recent is null)
                {
                    uow.Alerts.Add(new Alert
                    {
                        UserId = adminUserId,
                        Type = AlertType.OperationalWarning,
                        Severity = result.Report.OverallStatus == "Unhealthy"
                            ? AlertSeverity.Critical
                            : AlertSeverity.Warning,
                        Message = description.Length > 2000 ? description[..2000] : description,
                    });
                }
            }
        }

        await uow.SaveAsync(ct);
    }

    private static async Task<string?> ResolveAdminUserIdAsync(IServiceScope scope, CancellationToken ct)
    {
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(AdminEmail);
        return user?.Id;
    }
}
