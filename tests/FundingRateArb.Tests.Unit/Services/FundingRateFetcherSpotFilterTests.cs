using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

#pragma warning disable CA2000 // Dispose objects before losing scope — ServiceProvider managed by test

namespace FundingRateArb.Tests.Unit.Services;

/// <summary>
/// Tests for FundingRateFetcher — spot connector exclusion.
/// Spot connectors must be excluded from the fetch loop (they have no funding rates).
/// Perp connectors must still be iterated.
/// </summary>
public class FundingRateFetcherSpotFilterTests
{
    private readonly Mock<IMarketDataCache> _cache = new();
    private readonly Mock<IFundingRateReadinessSignal> _readiness = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _hubContext = new();
    private readonly Mock<IHubClients<IDashboardClient>> _hubClients = new();
    private readonly Mock<IDashboardClient> _hubClient = new();

    private readonly Mock<IExchangeConnector> _perpConnector = new();
    private readonly Mock<IExchangeConnector> _spotConnector = new();
    private readonly Mock<IExchangeConnectorFactory> _factory = new();

    public FundingRateFetcherSpotFilterTests()
    {
        _perpConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Perp);
        _perpConnector.Setup(c => c.ExchangeName).Returns("Lighter");
        _perpConnector.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateDto>());

        _spotConnector.Setup(c => c.MarketType).Returns(ExchangeMarketType.Spot);
        _spotConnector.Setup(c => c.ExchangeName).Returns("Binance");
        _spotConnector.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateDto>());

        _cache.Setup(c => c.GetAllForExchange(It.IsAny<string>()))
            .Returns(new List<FundingRateDto>());
        _cache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns(true); // always REST path

        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);
        _hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_hubClient.Object);
        _hubClient.Setup(c => c.ReceiveFundingRateUpdate(It.IsAny<List<FundingRateDto>>()))
            .Returns(Task.CompletedTask);
    }

    private FundingRateFetcher CreateFetcher(params IExchangeConnector[] connectors)
    {
        _factory.Setup(f => f.GetAllConnectors()).Returns(connectors.ToList());

        // Build a minimal DI scope that satisfies all GetRequiredService calls in FetchAllAsync
        var mockUow = new Mock<IUnitOfWork>();
        var mockFundingRates = new Mock<IFundingRateRepository>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockAssets = new Mock<IAssetRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockDiscovery = new Mock<IAssetDiscoveryService>();
        var mockAnalytics = new Mock<ICoinGlassAnalyticsRepository>();

        mockUow.Setup(u => u.FundingRates).Returns(mockFundingRates.Object);
        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.Assets).Returns(mockAssets.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync(new List<Exchange>());
        mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(new List<Asset>());
        mockFundingRates.Setup(f => f.AddRange(It.IsAny<IEnumerable<FundingRateSnapshot>>()));
        mockFundingRates.Setup(f => f.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync(new List<ArbitragePosition>());
        mockDiscovery.Setup(d => d.EnsureAssetsExistAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockAnalytics.Setup(a => a.PruneOldSnapshotsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Skip hourly aggregation (minute 0 → returns immediately since minute < 5 check)
        mockFundingRates.Setup(f => f.HourlyAggregatesExistAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddScoped<IUnitOfWork>(_ => mockUow.Object);
        serviceCollection.AddScoped<IExchangeConnectorFactory>(_ => _factory.Object);
        serviceCollection.AddScoped<IAssetDiscoveryService>(_ => mockDiscovery.Object);
        serviceCollection.AddScoped<ICoinGlassAnalyticsRepository>(_ => mockAnalytics.Object);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        return new FundingRateFetcher(
            scopeFactory,
            _cache.Object,
            _readiness.Object,
            _hubContext.Object,
            NullLogger<FundingRateFetcher>.Instance);
    }

    // ── Spot connector excluded ──────────────────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_SpotConnector_GetFundingRatesNotCalled()
    {
        var sut = CreateFetcher(_perpConnector.Object, _spotConnector.Object);

        await sut.FetchAllAsync(CancellationToken.None);

        _spotConnector.Verify(
            c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "Spot connectors must not be iterated for funding rates");
    }

    // ── Perp connector iterated ──────────────────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_PerpConnector_GetFundingRatesCalled()
    {
        var sut = CreateFetcher(_perpConnector.Object, _spotConnector.Object);

        await sut.FetchAllAsync(CancellationToken.None);

        _perpConnector.Verify(
            c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "Perp connectors must still be iterated for funding rates");
    }

    // ── Spot-only list → no fetch loop iterations ────────────────────────────

    [Fact]
    public async Task FetchAllAsync_OnlySpotConnectors_NoFetchAttempted()
    {
        var sut = CreateFetcher(_spotConnector.Object);

        await sut.FetchAllAsync(CancellationToken.None);

        _spotConnector.Verify(
            c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "When only Spot connectors exist, no funding rate fetch should be attempted");
    }
}
