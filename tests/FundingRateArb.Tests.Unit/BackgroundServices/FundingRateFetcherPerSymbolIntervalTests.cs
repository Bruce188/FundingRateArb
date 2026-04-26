using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Common.Services;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

/// <summary>
/// Tests that FundingRateFetcher.FetchAllAsync upserts per-symbol funding intervals
/// into IAssetExchangeFundingIntervalRepository after the existing per-exchange modal
/// interval block, using the snapshot data already collected in the fetch cycle.
/// </summary>
public class FundingRateFetcherPerSymbolIntervalTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
    private readonly Mock<IServiceScope> _mockScope = new();
    private readonly Mock<IServiceProvider> _mockScopeProvider = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IExchangeRepository> _mockExchanges = new();
    private readonly Mock<IAssetRepository> _mockAssets = new();
    private readonly Mock<IFundingRateRepository> _mockFundingRates = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IExchangeConnectorFactory> _mockFactory = new();
    private readonly Mock<IExchangeConnector> _mockHyperliquid = new();
    private readonly Mock<IExchangeConnector> _mockAster = new();
    private readonly Mock<IFundingRateReadinessSignal> _mockReadinessSignal = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext = new();
    private readonly Mock<IHubClients<IDashboardClient>> _mockHubClients = new();
    private readonly Mock<IDashboardClient> _mockDashboardClient = new();
    private readonly Mock<IAssetDiscoveryService> _mockDiscovery = new();
    private readonly Mock<ICoinGlassAnalyticsRepository> _mockCoinGlass = new();

    // The repository under test wiring
    private readonly Mock<IAssetExchangeFundingIntervalRepository> _mockIntervalRepo = new();

    private readonly FundingRateFetcher _sut;

    private static readonly List<Exchange> Exchanges =
    [
        new Exchange { Id = 1, Name = "Hyperliquid", IsActive = true, FundingIntervalHours = 8 },
        new Exchange { Id = 3, Name = "Aster",       IsActive = true, FundingIntervalHours = 8 },
    ];

    private static readonly List<Asset> Assets =
    [
        new Asset { Id = 1, Symbol = "ETH", IsActive = true },
        new Asset { Id = 2, Symbol = "BTC", IsActive = true },
    ];

    public FundingRateFetcherPerSymbolIntervalTests()
    {
        // Wire scope factory
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(_mockFactory.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IAssetDiscoveryService))).Returns(_mockDiscovery.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(ICoinGlassAnalyticsRepository))).Returns(_mockCoinGlass.Object);

        // Wire the interval repository via scope (so tests compile before constructor is changed)
        _mockScopeProvider.Setup(p => p.GetService(typeof(IAssetExchangeFundingIntervalRepository)))
            .Returns(_mockIntervalRepo.Object);

        // Default discovery: no new assets
        _mockDiscovery.Setup(d => d.EnsureAssetsExistAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // CoinGlass prune no-op
        _mockCoinGlass.Setup(c => c.PruneOldSnapshotsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Wire UoW
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync(Exchanges);
        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(Assets);
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());
        _mockFundingRates.Setup(f => f.GetSnapshotsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateSnapshot>());
        _mockFundingRates.Setup(f => f.PurgeAggregatesOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _mockFundingRates
            .Setup(f => f.HourlyAggregatesExistAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Wire connectors — use only two to keep tests simple
        _mockHyperliquid.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockAster.Setup(c => c.ExchangeName).Returns("Aster");
        _mockFactory.Setup(f => f.GetAllConnectors())
            .Returns([_mockHyperliquid.Object, _mockAster.Object]);

        // Hub
        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
        _mockHubClients.Setup(c => c.Group("MarketData")).Returns(_mockDashboardClient.Object);
        _mockDashboardClient
            .Setup(d => d.ReceiveFundingRateUpdate(It.IsAny<List<FundingRateDto>>()))
            .Returns(Task.CompletedTask);

        // Empty WebSocket cache forces REST fallback
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetAllForExchange(It.IsAny<string>())).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(true);

        // Default interval-repo mocks (no-ops until verified)
        _mockIntervalRepo
            .Setup(r => r.UpsertManyAsync(It.IsAny<IEnumerable<(int, int, int, int?)>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIntervalRepo.Setup(r => r.InvalidateCache());

        _sut = new FundingRateFetcher(
            _mockScopeFactory.Object,
            mockCache.Object,
            _mockReadinessSignal.Object,
            _mockHubContext.Object,
            NullLogger<FundingRateFetcher>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FundingRateDto MakeRate(string exchange, string symbol, int? detectedIntervalHours) =>
        new()
        {
            ExchangeName = exchange,
            Symbol = symbol,
            RatePerHour = 0.0005m,
            RawRate = 0.0005m,
            MarkPrice = 3000m,
            DetectedFundingIntervalHours = detectedIntervalHours,
        };

    // ── UpsertManyAsync is called when detected intervals are present ──────────

    /// <summary>
    /// When rates from any exchange carry a positive DetectedFundingIntervalHours,
    /// FetchAllAsync must call UpsertManyAsync on the interval repository exactly once
    /// with at least one entry.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_WhenRatesHavePositiveDetectedInterval_CallsUpsertManyAsync()
    {
        // Arrange: one rate with a detected 8-hour interval on Aster/ETH
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRate("Aster", "ETH", detectedIntervalHours: 8)]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: UpsertManyAsync must be called with at least one entry
        _mockIntervalRepo.Verify(
            r => r.UpsertManyAsync(
                It.Is<IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)>>(
                    entries => entries.Any()),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "UpsertManyAsync must be called when rates carry a positive DetectedFundingIntervalHours");
    }

    /// <summary>
    /// The entry passed to UpsertManyAsync must contain the correct ExchangeId and AssetId
    /// resolved from the exchange/asset maps, and the correct IntervalHours value from
    /// the rate's DetectedFundingIntervalHours.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_UpsertEntries_ContainCorrectExchangeIdAssetIdAndInterval()
    {
        // Arrange: Aster (Id=3) / ETH (Id=1) with detected 4-hour interval
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRate("Aster", "ETH", detectedIntervalHours: 4)]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: the upserted entry must carry ExchangeId=3 (Aster), AssetId=1 (ETH), IntervalHours=4
        _mockIntervalRepo.Verify(
            r => r.UpsertManyAsync(
                It.Is<IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)>>(
                    entries => entries.Any(e =>
                        e.ExchangeId == 3 &&
                        e.AssetId == 1 &&
                        e.IntervalHours == 4)),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "UpsertManyAsync entry must carry the resolved ExchangeId, AssetId, and IntervalHours");
    }

    /// <summary>
    /// When multiple rates with positive detected intervals are present across assets,
    /// all of them should be included in a single UpsertManyAsync call.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_MultipleRatesWithDetectedIntervals_AllIncludedInSingleUpsert()
    {
        // Arrange: Aster with two symbols both reporting 8h interval
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Aster", "ETH", detectedIntervalHours: 8),
                MakeRate("Aster", "BTC", detectedIntervalHours: 8),
            ]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: UpsertManyAsync called once with 2 entries (ETH + BTC)
        _mockIntervalRepo.Verify(
            r => r.UpsertManyAsync(
                It.Is<IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)>>(
                    entries => entries.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Both ETH and BTC entries must be included in one UpsertManyAsync call");
    }

    // ── InvalidateCache is called after a successful upsert ───────────────────

    /// <summary>
    /// After UpsertManyAsync completes, InvalidateCache must be called so that
    /// downstream consumers (e.g. signal engine) see the updated intervals immediately.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_AfterUpsert_CallsInvalidateCache()
    {
        // Arrange
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRate("Aster", "ETH", detectedIntervalHours: 8)]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert
        _mockIntervalRepo.Verify(
            r => r.InvalidateCache(),
            Times.Once,
            "InvalidateCache must be called after UpsertManyAsync to keep consumers in sync");
    }

    // ── Filtering: only positive detected intervals reach UpsertManyAsync ────────

    /// <summary>
    /// When only some rates have positive DetectedFundingIntervalHours, only those
    /// rates must appear in the UpsertManyAsync call — null-interval rates are excluded.
    /// Currently FAILS (red) because UpsertManyAsync is never called at all.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_NullIntervalRatesAreExcluded_OnlyPositiveIntervalsUpserted()
    {
        // Arrange: ETH has a detected interval, BTC does not
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Aster", "ETH", detectedIntervalHours: 8),    // qualifies
                MakeRate("Aster", "BTC", detectedIntervalHours: null),  // excluded
            ]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: exactly 1 entry (ETH only, BTC filtered out)
        _mockIntervalRepo.Verify(
            r => r.UpsertManyAsync(
                It.Is<IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)>>(
                    entries => entries.Count() == 1 &&
                               entries.Single().AssetId == 1),   // ETH = Id 1
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Null-interval rates must be excluded; only qualifying rates should be upserted");
    }

    /// <summary>
    /// When DetectedFundingIntervalHours is zero (invalid sentinel), the rate must be
    /// excluded from the upsert — only positive (> 0) intervals qualify.
    /// Currently FAILS (red) because UpsertManyAsync is never called at all.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_ZeroIntervalRateExcluded_ValidRateStillUpserted()
    {
        // Arrange: ETH has a valid 8h interval; BTC has zero (invalid)
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Aster", "ETH", detectedIntervalHours: 8),   // qualifies
                MakeRate("Aster", "BTC", detectedIntervalHours: 0),   // invalid — excluded
            ]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: UpsertManyAsync called with exactly 1 entry (ETH only)
        _mockIntervalRepo.Verify(
            r => r.UpsertManyAsync(
                It.Is<IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)>>(
                    entries => entries.Count() == 1 &&
                               entries.Single().AssetId == 1),   // ETH = Id 1
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Zero-interval rates must be filtered out; only IntervalHours > 0 entries are valid");
    }

    /// <summary>
    /// When a mix of qualifying and non-qualifying rates is present, InvalidateCache
    /// must still be called (because there is at least one qualifying entry in the upsert).
    /// Currently FAILS (red) because neither UpsertManyAsync nor InvalidateCache is called.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_WithMixedRates_InvalidateCacheCalledWhenAnyEntryQualifies()
    {
        // Arrange: one qualifying rate (ETH, 8h) and one non-qualifying (BTC, null)
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Aster", "ETH", detectedIntervalHours: 8),    // qualifies
                MakeRate("Aster", "BTC", detectedIntervalHours: null),  // excluded
            ]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: InvalidateCache must be called because at least one entry qualified
        _mockIntervalRepo.Verify(
            r => r.InvalidateCache(),
            Times.Once,
            "InvalidateCache must be called whenever at least one qualifying entry was upserted");
    }

    // ── Cancellation token flows through ──────────────────────────────────────

    /// <summary>
    /// The cancellation token passed to FetchAllAsync must be forwarded to
    /// UpsertManyAsync — not a new/default token — so cancellation propagates correctly.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_ForwardsCancellationTokenToUpsertManyAsync()
    {
        // Arrange
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRate("Aster", "ETH", detectedIntervalHours: 8)]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        CancellationToken capturedToken = default;
        _mockIntervalRepo
            .Setup(r => r.UpsertManyAsync(It.IsAny<IEnumerable<(int, int, int, int?)>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<(int, int, int, int?)>, CancellationToken>((_, ct) => capturedToken = ct)
            .Returns(Task.CompletedTask);

        // Act
        await _sut.FetchAllAsync(token);

        // Assert: the same token (not a default/new one) must have been forwarded
        capturedToken.Should().Be(token,
            "FetchAllAsync must forward its cancellation token to UpsertManyAsync, not introduce a new one");
    }

    // ── No upsert when all detected intervals are null ────────────────────────

    /// <summary>
    /// When every rate returned by connectors has DetectedFundingIntervalHours == null,
    /// FetchAllAsync must NOT call UpsertManyAsync on the interval repository.
    /// The per-symbol upsert path must be skipped entirely so that valid persisted intervals
    /// are not overwritten with nulls (which would be silently dropped anyway) and no
    /// unnecessary write is performed.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_WhenAllRatesHaveNullDetectedInterval_UpsertManyAsyncIsNeverCalled()
    {
        // Arrange: all rates from both connectors carry null DetectedFundingIntervalHours
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Aster", "ETH", detectedIntervalHours: null),
                MakeRate("Aster", "BTC", detectedIntervalHours: null),
            ]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Hyperliquid", "ETH", detectedIntervalHours: null),
                MakeRate("Hyperliquid", "BTC", detectedIntervalHours: null),
            ]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: no upsert when all intervals are unknown
        _mockIntervalRepo.Verify(
            r => r.UpsertManyAsync(
                It.IsAny<IEnumerable<(int, int, int, int?)>>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "UpsertManyAsync must not be called when all rates have DetectedFundingIntervalHours == null");
    }

    /// <summary>
    /// Spec requirement: feed allRates with mixed DetectedFundingIntervalHours values
    /// (some null, some 4h, some 8h on Aster) and assert the captured batch contains
    /// exactly the non-null rates with the correct (ExchangeId, AssetId, IntervalHours) tuples.
    ///
    /// Aster (Id=3) returns ETH with 8h, BTC with 4h, and one null-interval rate.
    /// Hyperliquid returns all-null rates.
    /// Expected upsert batch: exactly 2 entries — (3,1,8) and (3,2,4).
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_MixedNullFourAndEightHourIntervalsOnAster_BatchContainsExactlyNonNullEntriesWithCorrectIntervals()
    {
        // Arrange: mixed intervals on Aster; Hyperliquid all-null
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Aster", "ETH", detectedIntervalHours: 8),   // qualifies → (3, 1, 8)
                MakeRate("Aster", "BTC", detectedIntervalHours: 4),   // qualifies → (3, 2, 4)
            ]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeRate("Hyperliquid", "ETH", detectedIntervalHours: null),  // excluded
                MakeRate("Hyperliquid", "BTC", detectedIntervalHours: null),  // excluded
            ]);

        // Capture what was passed to UpsertManyAsync
        IEnumerable<(int ExchangeId, int AssetId, int IntervalHours, int? SnapshotId)>? capturedBatch = null;
        _mockIntervalRepo
            .Setup(r => r.UpsertManyAsync(
                It.IsAny<IEnumerable<(int, int, int, int?)>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<(int, int, int, int?)>, CancellationToken>(
                (entries, _) => capturedBatch = entries.ToList())
            .Returns(Task.CompletedTask);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: exactly 2 non-null entries, each with the correct interval
        capturedBatch.Should().NotBeNull("UpsertManyAsync should have been called with a non-empty batch");
        capturedBatch!.Should().HaveCount(2,
            "only the two Aster rates with non-null DetectedFundingIntervalHours qualify");

        capturedBatch.Should().Contain(e => e.ExchangeId == 3 && e.AssetId == 1 && e.IntervalHours == 8,
            "Aster(Id=3)/ETH(Id=1) with 8h interval must be in the batch");
        capturedBatch.Should().Contain(e => e.ExchangeId == 3 && e.AssetId == 2 && e.IntervalHours == 4,
            "Aster(Id=3)/BTC(Id=2) with 4h interval must be in the batch");

        capturedBatch.Should().NotContain(e => e.ExchangeId == 1,
            "Hyperliquid (Id=1) rates had null intervals and must not appear in the batch");
    }

    // ── Upsert happens AFTER snapshot persistence ─────────────────────────────

    /// <summary>
    /// UpsertManyAsync must be called AFTER uow.SaveAsync so that snapshot IDs are
    /// already assigned when the interval repo receives them.
    /// </summary>
    [Fact]
    public async Task FetchAllAsync_CallsUpsertManyAsync_AfterSnapshotSave()
    {
        // Arrange: track call order
        var callOrder = new List<string>();

        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(_ => callOrder.Add("SaveAsync"))
            .ReturnsAsync(1);

        _mockIntervalRepo
            .Setup(r => r.UpsertManyAsync(It.IsAny<IEnumerable<(int, int, int, int?)>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<(int, int, int, int?)>, CancellationToken>((_, _) => callOrder.Add("UpsertManyAsync"))
            .Returns(Task.CompletedTask);

        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeRate("Aster", "ETH", detectedIntervalHours: 8)]);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: SaveAsync must appear in the call list before UpsertManyAsync
        callOrder.Should().Contain("SaveAsync");
        callOrder.Should().Contain("UpsertManyAsync");

        var saveIndex = callOrder.IndexOf("SaveAsync");
        var upsertIndex = callOrder.IndexOf("UpsertManyAsync");
        upsertIndex.Should().BeGreaterThan(saveIndex,
            "UpsertManyAsync must be called after uow.SaveAsync so snapshot IDs are populated");
    }
}
