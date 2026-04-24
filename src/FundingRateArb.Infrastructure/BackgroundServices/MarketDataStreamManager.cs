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
    private readonly IExchangeSupportedSymbolsCache _symbolsCache;
    private readonly ILogger<MarketDataStreamManager> _logger;

    public MarketDataStreamManager(
        IEnumerable<IMarketDataStream> streams,
        IServiceScopeFactory scopeFactory,
        IHubContext<DashboardHub, IDashboardClient> hubContext,
        IExchangeSupportedSymbolsCache symbolsCache,
        ILogger<MarketDataStreamManager> logger)
    {
        _streams = streams;
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _symbolsCache = symbolsCache;
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

            // Start all streams in parallel, filtering symbols per exchange
            var startTasks = _streams.Select(async stream =>
            {
                try
                {
                    var supported = await _symbolsCache.GetSupportedSymbolsAsync(stream.ExchangeName, ct);
                    List<string> filtered;
                    if (supported.Count > 0)
                    {
                        // NB8: single-pass partition — one HashSet.Contains call per element.
                        var filteredList = new List<string>(symbols.Count);
                        var skippedList = new List<string>();
                        foreach (var s in symbols)
                        {
                            if (supported.Contains(s))
                            {
                                filteredList.Add(s);
                            }
                            else
                            {
                                skippedList.Add(s);
                            }
                        }
                        filtered = filteredList;
                        if (skippedList.Count > 0)
                        {
                            // nit8: avoid Take(20) iterator — use string.Join overload with index+count.
                            var displayCount = Math.Min(skippedList.Count, 20);
                            var skippedDisplay = skippedList.Count <= 20
                                ? string.Join(", ", skippedList)
                                : string.Join(", ", skippedList, 0, displayCount) + $" (+{skippedList.Count - 20} more)";
                            _logger.LogInformation(
                                    "WebSocket stream {Exchange}: {FilteredCount}/{TotalCount} symbols supported, skipped {SkippedCount}: {Skipped}",
                                    stream.ExchangeName, filtered.Count, symbols.Count, skippedList.Count,
                                    skippedDisplay);
                        }
                    }
                    else
                    {
                        // B1: use .ToList() so each stream gets an independent list instance,
                        // preventing shared-reference mutation across parallel stream lambdas.
                        filtered = symbols.ToList();
                        _logger.LogWarning(
                            "No supported symbols loaded for {Exchange} — passing full list as fallback",
                            stream.ExchangeName);
                    }

                    // NB11: subscribe handlers BEFORE StartAsync so events emitted synchronously
                    //       during or immediately after StartAsync are not dropped.
                    stream.OnRateUpdate += rate => OnRateReceived(rate);
                    stream.OnDisconnected += (exchange, reason) =>
                        _logger.LogWarning("WebSocket disconnected: {Exchange} — {Reason}", exchange, reason);

                    await stream.StartAsync(filtered, ct);
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
