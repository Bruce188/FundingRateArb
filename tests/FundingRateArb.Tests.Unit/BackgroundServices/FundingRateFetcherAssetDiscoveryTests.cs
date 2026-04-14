using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Data;
using FundingRateArb.Infrastructure.Hubs;
using FundingRateArb.Infrastructure.Repositories;
using FundingRateArb.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class FundingRateFetcherAssetDiscoveryTests
{
    // ── Shared helpers ──────────────────────────────────────────────────────

    private static AppDbContext CreateContext(string? dbName = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    private static FundingRateFetcher CreateFetcher(
        IServiceScopeFactory scopeFactory,
        IMarketDataCache? cache = null)
    {
        var mockCache = cache ?? CreateStaleCache();
        var mockHub = new Mock<IHubContext<DashboardHub, IDashboardClient>>();
        var mockClients = new Mock<IHubClients<IDashboardClient>>();
        var mockDashboard = new Mock<IDashboardClient>();
        mockHub.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockDashboard.Object);
        mockDashboard.Setup(d => d.ReceiveFundingRateUpdate(It.IsAny<List<FundingRateDto>>()))
            .Returns(Task.CompletedTask);

        return new FundingRateFetcher(
            scopeFactory,
            mockCache,
            new Mock<IFundingRateReadinessSignal>().Object,
            mockHub.Object,
            NullLogger<FundingRateFetcher>.Instance);
    }

    /// Creates a mock IMarketDataCache that always forces REST fallback.
    private static IMarketDataCache CreateStaleCache()
    {
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetAllForExchange(It.IsAny<string>())).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(true);
        return mockCache.Object;
    }

    private static List<FundingRateDto> MakeRates(string exchange, params string[] symbols) =>
        symbols.Select(s => new FundingRateDto
        {
            ExchangeName = exchange,
            Symbol = s,
            RatePerHour = 0.0005m,
            RawRate = 0.0005m,
            MarkPrice = 3000m,
        }).ToList();

    // ── Test: EnsureAssetsExistAsync called with all fetched symbols ──────────

    [Fact]
    public async Task FetchAllAsync_CallsDiscoveryWithFetchedSymbols()
    {
        // Arrange — mock everything except IAssetDiscoveryService
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockAssets = new Mock<IAssetRepository>();
        var mockFundingRates = new Mock<IFundingRateRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockFactory = new Mock<IExchangeConnectorFactory>();
        var mockConnector = new Mock<IExchangeConnector>();
        var mockDiscovery = new Mock<IAssetDiscoveryService>();

        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        mockProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(mockFactory.Object);
        mockProvider.Setup(p => p.GetService(typeof(IAssetDiscoveryService))).Returns(mockDiscovery.Object);

        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.Assets).Returns(mockAssets.Object);
        mockUow.Setup(u => u.FundingRates).Returns(mockFundingRates.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockExchanges.Setup(e => e.GetActiveAsync())
            .ReturnsAsync([new Exchange { Id = 1, Name = "Lighter", IsActive = true }]);
        mockAssets.Setup(a => a.GetActiveAsync())
            .ReturnsAsync([
                new Asset { Id = 1, Symbol = "BTC", IsActive = true },
                new Asset { Id = 2, Symbol = "YZY", IsActive = true }
            ]);
        mockFundingRates.Setup(f => f.GetSnapshotsInRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockFundingRates.Setup(f => f.PurgeAggregatesOlderThanAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockFundingRates.Setup(f => f.HourlyAggregatesExistAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        mockConnector.Setup(c => c.ExchangeName).Returns("Lighter");
        mockConnector.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "BTC", "YZY"));
        mockFactory.Setup(f => f.GetAllConnectors()).Returns([mockConnector.Object]);

        mockDiscovery.Setup(d => d.EnsureAssetsExistAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sut = CreateFetcher(mockScopeFactory.Object);

        // Act
        await sut.FetchAllAsync(CancellationToken.None);

        // Assert: discovery was called with a sequence containing both fetched symbols
        mockDiscovery.Verify(
            d => d.EnsureAssetsExistAsync(
                It.Is<IEnumerable<string>>(s => s.Contains("BTC") && s.Contains("YZY")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test: Discovery runs before asset map lookup (MockSequence) ──────────

    [Fact]
    public async Task FetchAllAsync_DiscoveryRunsBeforeAssetMapLookup()
    {
        // NB9: uses Moq's MockSequence, which raises immediately on an out-of-order
        // invocation rather than silently recording the first-seen order.
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockAssets = new Mock<IAssetRepository>(MockBehavior.Strict);
        var mockFundingRates = new Mock<IFundingRateRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockFactory = new Mock<IExchangeConnectorFactory>();
        var mockConnector = new Mock<IExchangeConnector>();
        var mockDiscovery = new Mock<IAssetDiscoveryService>(MockBehavior.Strict);

        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        mockProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(mockFactory.Object);
        mockProvider.Setup(p => p.GetService(typeof(IAssetDiscoveryService))).Returns(mockDiscovery.Object);

        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.Assets).Returns(mockAssets.Object);
        mockUow.Setup(u => u.FundingRates).Returns(mockFundingRates.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync([]);
        mockFundingRates.Setup(f => f.GetSnapshotsInRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockFundingRates.Setup(f => f.PurgeAggregatesOlderThanAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockFundingRates.Setup(f => f.HourlyAggregatesExistAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        // Enforce strict ordering: discovery MUST be called before GetActiveAsync.
        var seq = new MockSequence();
        mockDiscovery
            .InSequence(seq)
            .Setup(d => d.EnsureAssetsExistAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockAssets
            .InSequence(seq)
            .Setup(a => a.GetActiveAsync())
            .ReturnsAsync([]);

        mockConnector.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        mockConnector.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        mockFactory.Setup(f => f.GetAllConnectors()).Returns([mockConnector.Object]);

        var sut = CreateFetcher(mockScopeFactory.Object);

        // Act — any out-of-order call would throw MockException here.
        await sut.FetchAllAsync(CancellationToken.None);

        mockDiscovery.Verify(
            d => d.EnsureAssetsExistAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockAssets.Verify(a => a.GetActiveAsync(), Times.Once);
    }

    // ── NB8: fetcher forwards the exact CancellationToken instance ──────────

    [Fact]
    public async Task FetchAllAsync_ForwardsCancellationTokenToDiscovery()
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockAssets = new Mock<IAssetRepository>();
        var mockFundingRates = new Mock<IFundingRateRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockFactory = new Mock<IExchangeConnectorFactory>();
        var mockConnector = new Mock<IExchangeConnector>();
        var mockDiscovery = new Mock<IAssetDiscoveryService>();

        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        mockProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(mockFactory.Object);
        mockProvider.Setup(p => p.GetService(typeof(IAssetDiscoveryService))).Returns(mockDiscovery.Object);

        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.Assets).Returns(mockAssets.Object);
        mockUow.Setup(u => u.FundingRates).Returns(mockFundingRates.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync([]);
        mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync([]);
        mockFundingRates.Setup(f => f.GetSnapshotsInRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockFundingRates.Setup(f => f.PurgeAggregatesOlderThanAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockFundingRates.Setup(f => f.HourlyAggregatesExistAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        mockConnector.Setup(c => c.ExchangeName).Returns("Lighter");
        mockConnector.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "BTC"));
        mockFactory.Setup(f => f.GetAllConnectors()).Returns([mockConnector.Object]);

        mockDiscovery
            .Setup(d => d.EnsureAssetsExistAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sut = CreateFetcher(mockScopeFactory.Object);
        using var cts = new CancellationTokenSource();

        // Act
        await sut.FetchAllAsync(cts.Token);

        // Verify the EXACT token instance (not It.IsAny<CancellationToken>()) reached discovery.
        mockDiscovery.Verify(
            d => d.EnsureAssetsExistAsync(It.IsAny<IEnumerable<string>>(), cts.Token),
            Times.Once);
    }

    // ── NB10: empty-rates case (defensive behaviour) ────────────────────────

    [Fact]
    public async Task FetchAllAsync_NoRatesFetched_DiscoveryHandlesEmptyGracefully()
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();
        var mockUow = new Mock<IUnitOfWork>();
        var mockExchanges = new Mock<IExchangeRepository>();
        var mockAssets = new Mock<IAssetRepository>();
        var mockFundingRates = new Mock<IFundingRateRepository>();
        var mockPositions = new Mock<IPositionRepository>();
        var mockFactory = new Mock<IExchangeConnectorFactory>();
        var mockDiscovery = new Mock<IAssetDiscoveryService>();

        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        mockProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(mockUow.Object);
        mockProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(mockFactory.Object);
        mockProvider.Setup(p => p.GetService(typeof(IAssetDiscoveryService))).Returns(mockDiscovery.Object);

        mockUow.Setup(u => u.Exchanges).Returns(mockExchanges.Object);
        mockUow.Setup(u => u.Assets).Returns(mockAssets.Object);
        mockUow.Setup(u => u.FundingRates).Returns(mockFundingRates.Object);
        mockUow.Setup(u => u.Positions).Returns(mockPositions.Object);
        mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync([]);
        mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync([]);
        mockFundingRates.Setup(f => f.GetSnapshotsInRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockFundingRates.Setup(f => f.PurgeAggregatesOlderThanAsync(
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockFundingRates.Setup(f => f.HourlyAggregatesExistAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([]);

        mockFactory.Setup(f => f.GetAllConnectors()).Returns([]);

        mockDiscovery
            .Setup(d => d.EnsureAssetsExistAsync(
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var sut = CreateFetcher(mockScopeFactory.Object);

        var act = () => sut.FetchAllAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        mockDiscovery.Verify(
            d => d.EnsureAssetsExistAsync(
                It.Is<IEnumerable<string>>(s => !s.Any()),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test: End-to-end snapshot persistence for a newly-discovered symbol ──

    [Fact]
    public async Task FetchAllAsync_AfterDiscovery_PersistsSnapshotForNewSymbol()
    {
        // NB10: swapped from UseInMemoryDatabase + reflection-based _lastPurgeUtc to
        // SQLite in-memory. SQLite supports ExecuteDelete() so the hourly purge path
        // runs naturally — no private-field bypass needed. A rename of the field
        // will no longer silently break this test.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var schemaCtx = new AppDbContext(options);
        await schemaCtx.Database.EnsureCreatedAsync();

        await using var context = new AppDbContext(options);
        var exchange = new Exchange
        {
            Name = "Lighter",
            ApiBaseUrl = "https://api.lighter.xyz",
            WsBaseUrl = "wss://api.lighter.xyz/ws",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true
        };
        context.Exchanges.Add(exchange);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var cache = new MemoryCache(new MemoryCacheOptions());
        var uow = new UnitOfWork(context, cache);
        var assetRepo = new AssetRepository(context, cache);
        var discoveryService = new AssetDiscoveryService(
            assetRepo, context, NullLogger<AssetDiscoveryService>.Instance);

        // Wire scope factory so FundingRateFetcher resolves UoW and discovery from scope
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        var mockScope = new Mock<IServiceScope>();
        var mockProvider = new Mock<IServiceProvider>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
        mockScope.Setup(s => s.ServiceProvider).Returns(mockProvider.Object);
        mockProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(uow);
        mockProvider.Setup(p => p.GetService(typeof(IAssetDiscoveryService))).Returns(discoveryService);

        // Mock connector: returns one YZY rate from Lighter
        var mockFactory = new Mock<IExchangeConnectorFactory>();
        var mockConnector = new Mock<IExchangeConnector>();
        mockConnector.Setup(c => c.ExchangeName).Returns("Lighter");
        mockConnector.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "YZY"));
        mockFactory.Setup(f => f.GetAllConnectors()).Returns([mockConnector.Object]);
        mockProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(mockFactory.Object);

        var sut = CreateFetcher(mockScopeFactory.Object);

        // Act
        await sut.FetchAllAsync(CancellationToken.None);

        // Assert: (a) a new Asset row for YZY exists
        var yzyAsset = await context.Assets.FirstOrDefaultAsync(a => a.Symbol == "YZY");
        yzyAsset.Should().NotBeNull("AssetDiscoveryService should have created the YZY asset row");
        yzyAsset!.IsActive.Should().BeTrue();

        // Assert: (b) one FundingRateSnapshot row with the YZY asset id exists
        var snapshot = await context.FundingRateSnapshots
            .FirstOrDefaultAsync(s => s.AssetId == yzyAsset.Id);
        snapshot.Should().NotBeNull("a FundingRateSnapshot should be persisted for the newly-discovered YZY asset");
        snapshot!.ExchangeId.Should().Be(exchange.Id);
    }
}
