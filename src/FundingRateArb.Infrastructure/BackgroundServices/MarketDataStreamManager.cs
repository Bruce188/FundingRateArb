using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Infrastructure.BackgroundServices;

public class MarketDataStreamManager : BackgroundService
{
    private readonly IEnumerable<IMarketDataStream> _streams;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DashboardHub, IDashboardClient> _hubContext;
    private readonly ILogger<MarketDataStreamManager> _logger;

    public MarketDataStreamManager(
        IEnumerable<IMarketDataStream> streams,
        IServiceScopeFactory scopeFactory,
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        ILogger<MarketDataStreamManager> logger)
    {
        _streams = streams;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            // Load active asset symbols from DB
            var symbols = await LoadActiveSymbolsAsync(ct);
            if (symbols.Count == 0)
            {
                _logger.LogWarning("No active assets found — WebSocket streams will not start");
                return;
            }

            _logger.LogInformation("Starting WebSocket streams for {Count} symbols: {Symbols}",
                symbols.Count, string.Join(", ", symbols));

            // Start all streams in parallel
            var startTasks = _streams.Select(async stream =>
            {
                try
                {
                    await stream.StartAsync(symbols, ct);
                    stream.OnRateUpdate += rate => OnRateReceived(rate);
                    stream.OnDisconnected += (exchange, reason) =>
                        _logger.LogWarning("WebSocket disconnected: {Exchange} — {Reason}", exchange, reason);

                    _logger.LogInformation("WebSocket stream started: {Exchange}", stream.ExchangeName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start WebSocket stream for {Exchange}", stream.ExchangeName);
                }
            });

            await Task.WhenAll(startTasks);

            // Health monitoring loop
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    foreach (var stream in _streams)
                    {
                        if (!stream.IsConnected)
                        {
                            _logger.LogWarning("WebSocket stream disconnected: {Exchange}", stream.ExchangeName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Health check iteration failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — not an error
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "MarketDataStreamManager crashed — WebSocket streams will not recover");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping WebSocket streams...");

        // Stop all streams — wrap each in try/catch so one failure doesn't block others
        await Task.WhenAll(_streams.Select(async stream =>
        {
            try
            {
                await stream.StopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop WebSocket stream: {Exchange}", stream.ExchangeName);
            }
        }));

        // Dispose all streams — again wrap each individually
        foreach (var stream in _streams)
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispose WebSocket stream: {Exchange}", stream.ExchangeName);
            }
        }

        _logger.LogInformation("WebSocket streams stopped");

        await base.StopAsync(cancellationToken);
    }

    private void OnRateReceived(FundingRateDto rate)
    {
        _ = PushRateToSignalRAsync(rate);
    }

    private async Task PushRateToSignalRAsync(FundingRateDto rate)
    {
        try
        {
            await _hubContext.Clients.Group(HubGroups.MarketData)
                .ReceiveFundingRateUpdate(new List<FundingRateDto> { rate });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to push WebSocket rate update via SignalR");
        }
    }

    private async Task<List<string>> LoadActiveSymbolsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var assets = await uow.Assets.GetActiveAsync();
        return assets.Select(a => a.Symbol).ToList();
    }
}
