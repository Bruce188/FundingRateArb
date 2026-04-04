using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class FundingRateFetcherTests
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
    private readonly Mock<IExchangeConnector> _mockLighter = new();
    private readonly Mock<IExchangeConnector> _mockAster = new();
    private readonly Mock<IFundingRateReadinessSignal> _mockReadinessSignal = new();
    private readonly Mock<IHubContext<DashboardHub, IDashboardClient>> _mockHubContext = new();
    private readonly Mock<IHubClients<IDashboardClient>> _mockHubClients = new();
    private readonly Mock<IDashboardClient> _mockDashboardClient = new();
    private readonly FundingRateFetcher _sut;

    private static readonly List<Exchange> Exchanges =
    [
        new Exchange { Id = 1, Name = "Hyperliquid", IsActive = true },
        new Exchange { Id = 2, Name = "Lighter",     IsActive = true },
        new Exchange { Id = 3, Name = "Aster",        IsActive = true },
    ];

    private static readonly List<Asset> Assets =
    [
        new Asset { Id = 1, Symbol = "ETH", IsActive = true },
        new Asset { Id = 2, Symbol = "BTC", IsActive = true },
    ];

    public FundingRateFetcherTests()
    {
        // Wire scope factory — no ISignalEngine (H8: removed from fetcher)
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IExchangeConnectorFactory))).Returns(_mockFactory.Object);

        // Wire UoW
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchanges.Object);
        _mockUow.Setup(u => u.Assets).Returns(_mockAssets.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRates.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        // Default: no open positions for funding accumulation
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync(new List<ArbitragePosition>());
        _mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync(Exchanges);
        _mockAssets.Setup(a => a.GetActiveAsync()).ReturnsAsync(Assets);
        // Default: no snapshots for hourly aggregation
        _mockFundingRates.Setup(f => f.GetSnapshotsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateSnapshot>());
        _mockFundingRates.Setup(f => f.PurgeAggregatesOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _mockFundingRates
            .Setup(f => f.HourlyAggregatesExistAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Wire connectors
        _mockHyperliquid.Setup(c => c.ExchangeName).Returns("Hyperliquid");
        _mockLighter.Setup(c => c.ExchangeName).Returns("Lighter");
        _mockAster.Setup(c => c.ExchangeName).Returns("Aster");
        _mockFactory.Setup(f => f.GetAllConnectors())
            .Returns([_mockHyperliquid.Object, _mockLighter.Object, _mockAster.Object]);

        // Wire hub
        _mockHubContext.Setup(h => h.Clients).Returns(_mockHubClients.Object);
        _mockHubClients.Setup(c => c.Group("MarketData")).Returns(_mockDashboardClient.Object);
        _mockDashboardClient
            .Setup(d => d.ReceiveFundingRateUpdate(It.IsAny<List<FundingRateDto>>()))
            .Returns(Task.CompletedTask);

        // Empty cache forces REST fallback (preserves existing test behavior)
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetAllForExchange(It.IsAny<string>())).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(true);

        _sut = new FundingRateFetcher(
            _mockScopeFactory.Object,
            mockCache.Object,
            _mockReadinessSignal.Object,
            _mockHubContext.Object,
            NullLogger<FundingRateFetcher>.Instance);
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

    // ── TryAggregateHourly_OnDuplicateKeyException_ReturnsEarlySkipsPurge ─────

    [Fact]
    public async Task TryAggregateHourly_OnDuplicateKeyException_ReturnsEarlySkipsPurge()
    {
        // Use a fixed time with Minute >= 5 to bypass the minute guard deterministically
        var fixedNow = new DateTime(2026, 1, 15, 10, 10, 0, DateTimeKind.Utc);

        // Arrange: provide snapshots so aggregation is attempted
        var snapshot = new FundingRateSnapshot
        {
            ExchangeId = 1,
            AssetId = 1,
            RatePerHour = 0.001m,
            MarkPrice = 3000m,
            Volume24hUsd = 1_000_000m,
            RecordedAt = fixedNow.AddHours(-1),
        };
        _mockFundingRates
            .Setup(f => f.GetSnapshotsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { snapshot });

        // SaveAsync throws DbUpdateException (duplicate key) on first call
        _mockUow
            .Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Microsoft.EntityFrameworkCore.DbUpdateException(
                "Duplicate key", new Exception("inner")));

        // Act — pass nowOverride to bypass the wall-clock minute guard
        await _sut.TryAggregateHourlyAsync(_mockUow.Object, CancellationToken.None, nowOverride: fixedNow);

        // Assert: purge should NOT have been called — early return prevents it
        _mockFundingRates.Verify(
            f => f.PurgeAggregatesOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryAggregateHourly_OnDuplicateKeyException_PreventsRetryOnNextCycle()
    {
        // Use a fixed time with Minute >= 5 to bypass the minute guard deterministically
        var fixedNow = new DateTime(2026, 1, 15, 10, 10, 0, DateTimeKind.Utc);

        // Arrange: provide snapshots so aggregation is attempted
        var snapshot = new FundingRateSnapshot
        {
            ExchangeId = 1,
            AssetId = 1,
            RatePerHour = 0.001m,
            MarkPrice = 3000m,
            Volume24hUsd = 1_000_000m,
            RecordedAt = fixedNow.AddHours(-1),
        };
        _mockFundingRates
            .Setup(f => f.GetSnapshotsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateSnapshot> { snapshot });

        // SaveAsync throws DbUpdateException (duplicate key)
        _mockUow
            .Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Microsoft.EntityFrameworkCore.DbUpdateException(
                "Duplicate key", new Exception("inner")));

        // Act — first call hits duplicate key and should mark hour as aggregated
        await _sut.TryAggregateHourlyAsync(_mockUow.Object, CancellationToken.None, nowOverride: fixedNow);

        // Reset SaveAsync to not throw (simulate next cycle)
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        // Second call with same hour — should skip because _lastAggregatedHourUtc was set
        await _sut.TryAggregateHourlyAsync(_mockUow.Object, CancellationToken.None, nowOverride: fixedNow);

        // Assert: GetSnapshotsInRangeAsync should only be called once (first attempt),
        // because the second call exits early at the _lastAggregatedHourUtc guard
        _mockFundingRates.Verify(
            f => f.GetSnapshotsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── TryAggregateHourly_WhenAggregatesAlreadyExist_SkipsInsert ────────────────

    [Fact]
    public async Task TryAggregateHourly_WhenAggregatesAlreadyExist_SkipsInsert()
    {
        var fixedNow = new DateTime(2026, 1, 15, 10, 10, 0, DateTimeKind.Utc);

        // Aggregates already exist for this hour
        _mockFundingRates
            .Setup(f => f.HourlyAggregatesExistAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.TryAggregateHourlyAsync(_mockUow.Object, CancellationToken.None, nowOverride: fixedNow);

        // Should NOT fetch snapshots (existence check runs first)
        _mockFundingRates.Verify(
            f => f.GetSnapshotsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Should NOT attempt to add aggregates
        _mockFundingRates.Verify(
            f => f.AddAggregateRange(It.IsAny<IEnumerable<FundingRateHourlyAggregate>>()),
            Times.Never);
        // Should NOT call SaveAsync
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);

        // NB1: Second call — should skip at _lastAggregatedHourUtc guard (no DB queries)
        await _sut.TryAggregateHourlyAsync(_mockUow.Object, CancellationToken.None, nowOverride: fixedNow);
        _mockFundingRates.Verify(
            f => f.HourlyAggregatesExistAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── FetchAll_CallsAllThreeConnectors ───────────────────────────────────────

    [Fact]
    public async Task FetchAll_CallsAllThreeConnectors()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "ETH"));
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Aster", "ETH"));

        await _sut.FetchAllAsync(CancellationToken.None);

        _mockHyperliquid.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockLighter.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockAster.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FetchAll_WhenOneExchangeFails_ContinuesWithOthers ──────────────────────

    [Fact]
    public async Task FetchAll_WhenOneExchangeFails_ContinuesWithOthers()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API down"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "ETH"));
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Aster", "ETH"));

        // Should not throw
        var act = async () => await _sut.FetchAllAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Other connectors still called
        _mockLighter.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockAster.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FetchAll_SavesSnapshotsViaUnitOfWork ───────────────────────────────────

    [Fact]
    public async Task FetchAll_SavesSnapshotsViaUnitOfWork()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH", "BTC"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.FetchAllAsync(CancellationToken.None);

        // 2 rates mapped → AddRange called once with 2 snapshots
        _mockFundingRates.Verify(
            f => f.AddRange(It.Is<IEnumerable<FundingRateSnapshot>>(s => s.Count() == 2)),
            Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── FetchAll_MapsRateDtoToSnapshotEntity_Correctly ────────────────────────

    [Fact]
    public async Task FetchAll_MapsRateDtoToSnapshotEntity_Correctly()
    {
        var rate = new FundingRateDto
        {
            ExchangeName = "Hyperliquid",
            Symbol = "ETH",
            RatePerHour = 0.0004m,
            RawRate = 0.0004m,
            MarkPrice = 2999m,
            IndexPrice = 2998m,
            Volume24hUsd = 5_000_000m,
        };
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([rate]);
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        List<FundingRateSnapshot>? savedList = null;
        _mockFundingRates.Setup(f => f.AddRange(It.IsAny<IEnumerable<FundingRateSnapshot>>()))
            .Callback<IEnumerable<FundingRateSnapshot>>(s => savedList = s.ToList());

        await _sut.FetchAllAsync(CancellationToken.None);

        savedList.Should().NotBeNull();
        savedList.Should().HaveCount(1);
        var saved = savedList![0];
        saved.ExchangeId.Should().Be(1);   // Hyperliquid = Id 1
        saved.AssetId.Should().Be(1);        // ETH = Id 1
        saved.RatePerHour.Should().Be(0.0004m);
        saved.MarkPrice.Should().Be(2999m);
        saved.Volume24hUsd.Should().Be(5_000_000m);
    }

    // ── FetchAll_IgnoresUnknownSymbols ─────────────────────────────────────────

    [Fact]
    public async Task FetchAll_IgnoresUnknownSymbols()
    {
        // "DOGE" is not in Assets list
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "DOGE"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.FetchAllAsync(CancellationToken.None);

        _mockFundingRates.Verify(f => f.Add(It.IsAny<FundingRateSnapshot>()), Times.Never);
    }

    // ── FetchAll_PushesUpdateViaSignalR ────────────────────────────────────────

    [Fact]
    public async Task FetchAll_PushesUpdateViaSignalR()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.FetchAllAsync(CancellationToken.None);

        _mockDashboardClient.Verify(
            d => d.ReceiveFundingRateUpdate(It.IsAny<List<FundingRateDto>>()),
            Times.Once);
    }

    // ── H8: FundingRateFetcher does NOT push opportunities (SRP violation removed) ─

    [Fact]
    public async Task FetchAll_DoesNotPushOpportunityUpdate()
    {
        // H8: Opportunity computation and push moved to BotOrchestrator.
        // FundingRateFetcher must only fetch rates and push ReceiveFundingRateUpdate.
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.FetchAllAsync(CancellationToken.None);

        _mockDashboardClient.Verify(
            d => d.ReceiveOpportunityUpdate(It.IsAny<OpportunityResultDto>()),
            Times.Never,
            "FundingRateFetcher must not call ReceiveOpportunityUpdate — that responsibility belongs to BotOrchestrator");
    }

    // ── H-FR1: Purge is called during FetchAll with 48h cutoff ────────────────

    [Fact]
    public async Task FetchAll_CallsPurgeOlderThan_With48HourCutoff()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _mockFundingRates
            .Setup(f => f.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var before = DateTime.UtcNow;
        await _sut.FetchAllAsync(CancellationToken.None);
        var after = DateTime.UtcNow;

        _mockFundingRates.Verify(
            f => f.PurgeOlderThanAsync(
                It.Is<DateTime>(dt =>
                    dt >= before.AddHours(-48).AddSeconds(-5) &&
                    dt <= after.AddHours(-48).AddSeconds(5)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── H-FR1: Purge is NOT called on every tick (only hourly) ────────────────

    [Fact]
    public async Task FetchAll_DoesNotCallPurge_OnConsecutiveCallsWithinSameHour()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _mockFundingRates
            .Setup(f => f.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // First call — purge should happen
        await _sut.FetchAllAsync(CancellationToken.None);
        // Second call immediately after — purge should NOT happen again (within same hour)
        await _sut.FetchAllAsync(CancellationToken.None);

        // Purge called exactly once across two consecutive fetches
        _mockFundingRates.Verify(
            f => f.PurgeOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── H-FR2: Dictionary lookup used instead of O(N*M) FirstOrDefault scan ───
    // This is validated structurally: multiple rates for multiple assets map correctly
    // (if O(N*M) scan had a bug with case sensitivity, this would catch it)

    [Fact]
    public async Task FetchAll_MapsRatesUsingCaseInsensitiveLookup()
    {
        // Rate uses mixed-case "HYPERLIQUID" — should still match "Hyperliquid"
        var rateWithDifferentCase = new FundingRateDto
        {
            ExchangeName = "HYPERLIQUID",
            Symbol = "eth",           // lowercase — should match "ETH"
            RatePerHour = 0.0004m,
            RawRate = 0.0004m,
            MarkPrice = 3000m,
        };

        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([rateWithDifferentCase]);
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        List<FundingRateSnapshot>? saved = null;
        _mockFundingRates.Setup(f => f.AddRange(It.IsAny<IEnumerable<FundingRateSnapshot>>()))
            .Callback<IEnumerable<FundingRateSnapshot>>(s => saved = s.ToList());

        await _sut.FetchAllAsync(CancellationToken.None);

        // Case-insensitive lookup must find the exchange and asset
        saved.Should().NotBeNull();
        saved.Should().HaveCount(1, "case-insensitive match should succeed for 'HYPERLIQUID'/'eth'");
        saved![0].ExchangeId.Should().Be(1);
        saved[0].AssetId.Should().Be(1);
    }

    // ── D10: Funding accumulation delta ─────────────────────────────────────────

    [Fact]
    public async Task UpdateAccumulatedFunding_AccumulatesDelta()
    {
        // Open position: SizeUsdc=100, Leverage=5 → notional=500
        var pos = new ArbitragePosition
        {
            Id = 1,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            Leverage = 5,
            AccumulatedFunding = 0m,
            Status = PositionStatus.Open,
        };
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        // Latest rates: shortRate=0.001/hr, longRate=0.0002/hr
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0002m },
                new() { ExchangeId = 2, AssetId = 1, RatePerHour = 0.001m },
            });

        // Setup connectors to return rates
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.FetchAllAsync(CancellationToken.None);

        // notional=500, netRate=0.001-0.0002=0.0008, delta=500*0.0008/60 = 0.006667
        pos.AccumulatedFunding.Should().BeApproximately(0.00667m, 0.0001m);
    }

    // ── D10: Skip missing rates ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAccumulatedFunding_SkipsMissingRates()
    {
        var pos = new ArbitragePosition
        {
            Id = 1,
            AssetId = 1,
            LongExchangeId = 1,
            ShortExchangeId = 2,
            SizeUsdc = 100m,
            Leverage = 5,
            AccumulatedFunding = 5.0m,
            Status = PositionStatus.Open,
        };
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        // No matching rates for this position's exchanges
        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>());

        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _sut.FetchAllAsync(CancellationToken.None);

        // AccumulatedFunding should remain unchanged
        pos.AccumulatedFunding.Should().Be(5.0m);
    }

    // ── Hybrid WebSocket/REST Cache Tests ────────────────────────────────────

    [Fact]
    public async Task FetchAll_UsesCacheWhenFresh()
    {
        // Set up cache to return fresh data for all exchanges
        var cachedRates = MakeRates("Hyperliquid", "ETH");
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetAllForExchange(It.IsAny<string>())).Returns(cachedRates);
        mockCache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(false);

        var sut = new FundingRateFetcher(
            _mockScopeFactory.Object, mockCache.Object, _mockReadinessSignal.Object,
            _mockHubContext.Object, NullLogger<FundingRateFetcher>.Instance);

        await sut.FetchAllAsync(CancellationToken.None);

        // REST should NOT be called when cache is fresh
        _mockHyperliquid.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockLighter.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockAster.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FetchAll_FallsBackToRestWhenStale()
    {
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetAllForExchange(It.IsAny<string>())).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(true);

        var sut = new FundingRateFetcher(
            _mockScopeFactory.Object, mockCache.Object, _mockReadinessSignal.Object,
            _mockHubContext.Object, NullLogger<FundingRateFetcher>.Instance);

        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "ETH"));
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Aster", "ETH"));

        await sut.FetchAllAsync(CancellationToken.None);

        // REST should be called when cache is stale
        _mockHyperliquid.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockLighter.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockAster.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FetchAll_CachePreservesVolume_WhenWebSocketSendsZero()
    {
        // Simulates the real pipeline: REST populates cache with volume, then
        // WebSocket updates overwrite with 0 volume (Aster mark price stream).
        // The cache fix should preserve the REST-fetched volume.
        var realCache = new MarketDataCache();

        // Step 1: REST fetch populates cache with real volume
        realCache.Update(new FundingRateDto
        {
            ExchangeName = "Aster",
            Symbol = "ETH",
            RatePerHour = 0.0005m,
            RawRate = 0.002m,
            MarkPrice = 3000m,
            IndexPrice = 3000m,
            Volume24hUsd = 100_000m,
        });

        // Step 2: WebSocket update overwrites rate/price but has 0 volume
        realCache.Update(new FundingRateDto
        {
            ExchangeName = "Aster",
            Symbol = "ETH",
            RatePerHour = 0.0006m,
            RawRate = 0.0024m,
            MarkPrice = 3050m,
            IndexPrice = 3050m,
            Volume24hUsd = 0m,
        });

        // Step 3: FundingRateFetcher reads from cache (fresh, not stale)
        var cached = realCache.GetAllForExchange("Aster");
        cached.Should().HaveCount(1);

        var dto = cached[0];
        // Rate and price should be updated to WebSocket values
        dto.RatePerHour.Should().Be(0.0006m);
        dto.MarkPrice.Should().Be(3050m);
        // Volume should be preserved from the REST fetch
        dto.Volume24hUsd.Should().Be(100_000m);
    }

    [Fact]
    public async Task FetchAll_SeedsCacheFromRest_SoVolumeIsPreserved()
    {
        // Verifies the FundingRateFetcher writes REST results to the cache,
        // which is critical: without this, the cache volume-preservation fix
        // has nothing to preserve when WebSocket updates arrive with volume=0.
        var realCache = new MarketDataCache();

        var sut = new FundingRateFetcher(
            _mockScopeFactory.Object, realCache, _mockReadinessSignal.Object,
            _mockHubContext.Object, NullLogger<FundingRateFetcher>.Instance);

        var restRates = new List<FundingRateDto>
        {
            new() { ExchangeName = "Aster", Symbol = "ETH",
                     RatePerHour = 0.0005m, RawRate = 0.002m,
                     MarkPrice = 3000m, IndexPrice = 3000m,
                     Volume24hUsd = 100_000m },
        };

        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(restRates);
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // REST fetch should seed the cache
        await sut.FetchAllAsync(CancellationToken.None);

        var cached = realCache.GetLatest("Aster", "ETH");
        cached.Should().NotBeNull();
        cached!.Volume24hUsd.Should().Be(100_000m);

        // Simulate WebSocket update with 0 volume
        realCache.Update(new FundingRateDto
        {
            ExchangeName = "Aster",
            Symbol = "ETH",
            RatePerHour = 0.0006m,
            RawRate = 0.0024m,
            MarkPrice = 3050m,
            IndexPrice = 3050m,
            Volume24hUsd = 0m,
        });

        // Volume should be preserved from the REST-seeded entry
        realCache.GetLatest("Aster", "ETH")!.Volume24hUsd.Should().Be(100_000m);
    }

    [Fact]
    public async Task FetchAll_MixedMode_SomeCachedSomeRest()
    {
        var cachedHyperliquid = MakeRates("Hyperliquid", "ETH");

        var mockCache = new Mock<IMarketDataCache>();
        // Hyperliquid: fresh cache
        mockCache.Setup(c => c.GetAllForExchange("Hyperliquid")).Returns(cachedHyperliquid);
        mockCache.Setup(c => c.IsStaleForExchange("Hyperliquid", It.IsAny<TimeSpan>())).Returns(false);
        // Lighter & Aster: stale/empty
        mockCache.Setup(c => c.GetAllForExchange("Lighter")).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange("Lighter", It.IsAny<TimeSpan>())).Returns(true);
        mockCache.Setup(c => c.GetAllForExchange("Aster")).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange("Aster", It.IsAny<TimeSpan>())).Returns(true);

        var sut = new FundingRateFetcher(
            _mockScopeFactory.Object, mockCache.Object, _mockReadinessSignal.Object,
            _mockHubContext.Object, NullLogger<FundingRateFetcher>.Instance);

        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Lighter", "ETH"));
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Aster", "ETH"));

        await sut.FetchAllAsync(CancellationToken.None);

        // Hyperliquid: NOT called (cache was fresh)
        _mockHyperliquid.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Never);
        // Lighter + Aster: called (cache stale)
        _mockLighter.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockAster.Verify(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Settlement-aware funding accumulation tests ──────────────────────────

    [Theory]
    [InlineData(2026, 3, 22, 7, 30, 0, 8, 2026, 3, 22, 0, 0, 0)]  // 07:30 floors to 00:00 (8h intervals)
    [InlineData(2026, 3, 22, 8, 0, 0, 8, 2026, 3, 22, 8, 0, 0)]    // 08:00 floors to 08:00 exactly
    [InlineData(2026, 3, 22, 15, 59, 59, 8, 2026, 3, 22, 8, 0, 0)]  // 15:59 floors to 08:00
    [InlineData(2026, 3, 22, 16, 0, 0, 8, 2026, 3, 22, 16, 0, 0)]   // 16:00 floors to 16:00 exactly
    [InlineData(2026, 3, 22, 3, 30, 0, 4, 2026, 3, 22, 0, 0, 0)]    // 03:30 floors to 00:00 (4h intervals)
    [InlineData(2026, 3, 22, 4, 0, 0, 4, 2026, 3, 22, 4, 0, 0)]     // 04:00 floors to 04:00
    [InlineData(2026, 3, 22, 0, 30, 0, 1, 2026, 3, 22, 0, 0, 0)]    // 00:30 floors to 00:00 (1h intervals)
    public void FloorToSettlement_ReturnsCorrectBoundary(
        int yr, int mo, int dy, int hr, int mn, int sc, int intervalHours,
        int eyr, int emo, int edy, int ehr, int emn, int esc)
    {
        var input = new DateTime(yr, mo, dy, hr, mn, sc, DateTimeKind.Utc);
        var expected = new DateTime(eyr, emo, edy, ehr, emn, esc, DateTimeKind.Utc);

        var result = FundingRateFetcher.FloorToSettlement(input, intervalHours);

        result.Should().Be(expected);
    }

    [Fact]
    public void ComputeLegFunding_ContinuousExchange_ReturnsProRata()
    {
        // Continuous exchange: funding = notional * rate / 60
        var exchangeById = new Dictionary<int, Exchange>
        {
            [1] = new Exchange
            {
                Id = 1,
                Name = "Hyperliquid",
                FundingSettlementType = FundingSettlementType.Continuous,
                FundingIntervalHours = 1,
            }
        };

        var result = _sut.ComputeLegFunding(
            ratePerHour: 0.0005m,
            notional: 1000m,
            exchangeId: 1,
            exchangeById: exchangeById,
            now: DateTime.UtcNow);

        // 1000 * 0.0005 / 60 = 0.008333...
        result.Should().BeApproximately(0.00833m, 0.0001m);
    }

    [Fact]
    public void ComputeLegFunding_PeriodicExchange_FirstCycle_ReturnsZero()
    {
        // First cycle for a periodic exchange: no prior reference, returns 0
        var exchangeById = new Dictionary<int, Exchange>
        {
            [3] = new Exchange
            {
                Id = 3,
                Name = "Aster",
                FundingSettlementType = FundingSettlementType.Periodic,
                FundingIntervalHours = 8,
            }
        };

        var result = _sut.ComputeLegFunding(
            ratePerHour: 0.0005m,
            notional: 1000m,
            exchangeId: 3,
            exchangeById: exchangeById,
            now: DateTime.UtcNow);

        result.Should().Be(0m, "first cycle should return zero to avoid double-counting");
    }

    [Fact]
    public void ComputeLegFunding_PeriodicExchange_NoBoundaryCrossed_ReturnsZero()
    {
        // Periodic exchange: both last cycle and now are in the same settlement window
        var exchangeById = new Dictionary<int, Exchange>
        {
            [3] = new Exchange
            {
                Id = 3,
                Name = "Aster",
                FundingSettlementType = FundingSettlementType.Periodic,
                FundingIntervalHours = 8,
            }
        };

        // Simulate a previous cycle at 01:00 — settlement window is 00:00-08:00
        var sut = CreateFetcherWithSettlementState(exchangeId: 3,
            lastCycleTime: new DateTime(2026, 3, 22, 1, 0, 0, DateTimeKind.Utc));

        // Now is 02:00 — same settlement window, no boundary crossed
        var result = sut.ComputeLegFunding(
            ratePerHour: 0.0005m,
            notional: 1000m,
            exchangeId: 3,
            exchangeById: exchangeById,
            now: new DateTime(2026, 3, 22, 2, 0, 0, DateTimeKind.Utc));

        result.Should().Be(0m, "no settlement boundary was crossed");
    }

    [Fact]
    public void ComputeLegFunding_PeriodicExchange_BoundaryCrossed_ReturnsFullInterval()
    {
        // Periodic exchange: settlement boundary crossed between last cycle and now
        var exchangeById = new Dictionary<int, Exchange>
        {
            [3] = new Exchange
            {
                Id = 3,
                Name = "Aster",
                FundingSettlementType = FundingSettlementType.Periodic,
                FundingIntervalHours = 8,
            }
        };

        // Last cycle at 07:59 (settlement window 00:00-08:00)
        var sut = CreateFetcherWithSettlementState(exchangeId: 3,
            lastCycleTime: new DateTime(2026, 3, 22, 7, 59, 0, DateTimeKind.Utc));

        // Now is 08:01 — crossed the 08:00 boundary
        var result = sut.ComputeLegFunding(
            ratePerHour: 0.0005m,
            notional: 1000m,
            exchangeId: 3,
            exchangeById: exchangeById,
            now: new DateTime(2026, 3, 22, 8, 1, 0, DateTimeKind.Utc));

        // Full interval payment = notional * ratePerHour * intervalHours = 1000 * 0.0005 * 8 = 4.0
        result.Should().Be(4.0m);
    }

    [Fact]
    public async Task UpdateAccumulatedFunding_MixedExchanges_PeriodicLegSkippedBeforeSettlement()
    {
        // Position: long on Hyperliquid (Continuous), short on Aster (Periodic/8h)
        // When no settlement boundary has been crossed for Aster, only the Hyperliquid leg accumulates.
        var exchangesWithSettlement = new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid", IsActive = true,
                    FundingSettlementType = FundingSettlementType.Continuous, FundingIntervalHours = 1 },
            new() { Id = 2, Name = "Lighter",     IsActive = true,
                    FundingSettlementType = FundingSettlementType.Continuous, FundingIntervalHours = 1 },
            new() { Id = 3, Name = "Aster",       IsActive = true,
                    FundingSettlementType = FundingSettlementType.Periodic, FundingIntervalHours = 8 },
        };
        _mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync(exchangesWithSettlement);

        var pos = new ArbitragePosition
        {
            Id = 1,
            AssetId = 1,
            LongExchangeId = 1,   // Hyperliquid (Continuous)
            ShortExchangeId = 3,  // Aster (Periodic)
            SizeUsdc = 100m,
            Leverage = 5,
            AccumulatedFunding = 0m,
            Status = PositionStatus.Open,
        };
        _mockPositions.Setup(p => p.GetOpenTrackedAsync()).ReturnsAsync([pos]);

        _mockFundingRates.Setup(f => f.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(new List<FundingRateSnapshot>
            {
                new() { ExchangeId = 1, AssetId = 1, RatePerHour = 0.0002m },
                new() { ExchangeId = 3, AssetId = 1, RatePerHour = 0.001m },
            });

        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // First FetchAll: establishes settlement tracking, Aster returns 0 (first cycle)
        await _sut.FetchAllAsync(CancellationToken.None);

        // After first cycle: only Hyperliquid continuous leg subtracts, Aster first-cycle returns 0
        // Net = 0 (short/Aster first cycle) - (500 * 0.0002 / 60) = negative (long cost)
        // longFunding = 500 * 0.0002 / 60 = 0.001667
        // shortFunding = 0 (first cycle for periodic)
        // accumulated = 0 - 0.001667 = -0.001667
        pos.AccumulatedFunding.Should().BeApproximately(-0.00167m, 0.0001m);

        // Second FetchAll (same settlement window): Aster still returns 0
        pos.AccumulatedFunding = 0m; // Reset for clarity
        await _sut.FetchAllAsync(CancellationToken.None);

        // Both calls happen within the same 8h window, so Aster returns 0 again
        // Only Hyperliquid continuous leg accumulates
        pos.AccumulatedFunding.Should().BeApproximately(-0.00167m, 0.0001m);
    }

    [Fact]
    public void ComputeLegFunding_UnknownExchange_ReturnsZero()
    {
        var exchangeById = new Dictionary<int, Exchange>();
        var result = _sut.ComputeLegFunding(0.0005m, 1000m, 999, exchangeById, DateTime.UtcNow);
        result.Should().Be(0m);
    }

    // ── Readiness signal test ────────────────────────────────────────────────

    [Fact]
    public async Task FetchAll_SignalsReadyAfterFirstSuccessfulFetch()
    {
        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRates("Hyperliquid", "ETH"));
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var signalCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockReadinessSignal.Setup(r => r.SignalReady()).Callback(() => signalCalled.TrySetResult(true));

        // Start the background service — ExecuteAsync calls FetchAllAsync then SignalReadyOnce
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        await _sut.StartAsync(cts.Token);

        // Wait for SignalReady to be called (deterministic, no Task.Delay)
        await signalCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Verify SignalReady was called on the mock readiness signal
        _mockReadinessSignal.Verify(r => r.SignalReady(), Times.Once);

        await _sut.StopAsync(CancellationToken.None);
    }

    // ── Per-exchange funding accuracy tests ────────────────────────────────

    [Fact]
    public void ComputeNotional_OraclePriceExchange_UsesIndexPrice()
    {
        var pos = new ArbitragePosition { SizeUsdc = 100m, Leverage = 5, LongEntryPrice = 3000m };
        var rate = new FundingRateSnapshot { IndexPrice = 3100m, MarkPrice = 3050m };
        var exchange = new Exchange
        {
            Id = 1,
            Name = "Hyperliquid",
            FundingNotionalPriceType = FundingNotionalPriceType.OraclePrice,
        };

        var result = FundingRateFetcher.ComputeNotional(pos, pos.LongEntryPrice, rate, exchange);

        // quantity = 500 / 3000 = 0.16667, notional = 0.16667 * 3100 (IndexPrice) = 516.67
        var expected = (100m * 5m / 3000m) * 3100m;
        result.Should().BeApproximately(expected, 0.01m);
    }

    [Fact]
    public void ComputeNotional_MarkPriceExchange_UsesMarkPrice()
    {
        var pos = new ArbitragePosition { SizeUsdc = 100m, Leverage = 5, LongEntryPrice = 3000m };
        var rate = new FundingRateSnapshot { IndexPrice = 3100m, MarkPrice = 3050m };
        var exchange = new Exchange
        {
            Id = 2,
            Name = "Lighter",
            FundingNotionalPriceType = FundingNotionalPriceType.MarkPrice,
        };

        var result = FundingRateFetcher.ComputeNotional(pos, pos.LongEntryPrice, rate, exchange);

        // quantity = 500 / 3000 = 0.16667, notional = 0.16667 * 3050 (MarkPrice) = 508.33
        var expected = (100m * 5m / 3000m) * 3050m;
        result.Should().BeApproximately(expected, 0.01m);
    }

    [Fact]
    public void ApplyRebate_PayingLegWithRebate_ReducesFundingBy15Percent()
    {
        var exchange = new Exchange
        {
            Id = 2,
            Name = "Lighter",
            FundingRebateRate = 0.15m,
        };

        // Positive funding = paying side
        var rawFunding = 1.0m;
        var result = _sut.ApplyRebate(rawFunding, exchange);

        // 1.0 * (1 - 0.15) = 0.85
        result.Should().Be(0.85m);
    }

    [Fact]
    public void ApplyRebate_EarningLeg_NoRebateApplied()
    {
        var exchange = new Exchange
        {
            Id = 2,
            Name = "Lighter",
            FundingRebateRate = 0.15m,
        };

        // Negative funding = earning side — rebate should not apply
        var result = _sut.ApplyRebate(-1.0m, exchange);
        result.Should().Be(-1.0m);
    }

    [Fact]
    public void ApplyRebate_NoRebateExchange_ReturnsSameValue()
    {
        var exchange = new Exchange
        {
            Id = 1,
            Name = "Hyperliquid",
            FundingRebateRate = 0m,
        };

        var result = _sut.ApplyRebate(1.0m, exchange);
        result.Should().Be(1.0m);
    }

    [Fact]
    public async Task FetchAll_BinanceIntervalChanged_UpdatesExchangeEntity()
    {
        // Arrange: Binance exchange with 8h interval, but API returns 4h
        var binanceExchange = new Exchange
        {
            Id = 4, Name = "Binance", IsActive = true,
            FundingIntervalHours = 8,
            FundingSettlementType = FundingSettlementType.Periodic,
        };
        var exchangesWithBinance = new List<Exchange>(Exchanges) { binanceExchange };
        _mockExchanges.Setup(e => e.GetActiveAsync()).ReturnsAsync(exchangesWithBinance);

        // Mock a Binance connector
        var mockBinance = new Mock<IExchangeConnector>();
        mockBinance.Setup(c => c.ExchangeName).Returns("Binance");
        mockBinance.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FundingRateDto>
            {
                new()
                {
                    ExchangeName = "Binance",
                    Symbol = "ETH",
                    RatePerHour = 0.0001m,
                    RawRate = 0.0004m,
                    MarkPrice = 3000m,
                    DetectedFundingIntervalHours = 4,
                }
            });

        _mockHyperliquid.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockLighter.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _mockAster.Setup(c => c.GetFundingRatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _mockFactory.Setup(f => f.GetAllConnectors())
            .Returns([_mockHyperliquid.Object, _mockLighter.Object, _mockAster.Object, mockBinance.Object]);

        // Act
        await _sut.FetchAllAsync(CancellationToken.None);

        // Assert: exchange entity should be updated from 8h to 4h
        binanceExchange.FundingIntervalHours.Should().Be(4);
    }

    [Fact]
    public void DefaultExchange_FundingNotionalPriceType_IsMarkPrice()
    {
        var exchange = new Exchange();
        exchange.FundingNotionalPriceType.Should().Be(FundingNotionalPriceType.MarkPrice);
    }

    [Fact]
    public void DefaultExchange_FundingRebateRate_IsZero()
    {
        var exchange = new Exchange();
        exchange.FundingRebateRate.Should().Be(0m);
    }

    [Fact]
    public void DefaultExchange_FundingTimingDeviationSeconds_IsZero()
    {
        var exchange = new Exchange();
        exchange.FundingTimingDeviationSeconds.Should().Be(0);
    }

    /// <summary>
    /// Helper to create a FundingRateFetcher with pre-seeded settlement state for testing.
    /// </summary>
    private FundingRateFetcher CreateFetcherWithSettlementState(int exchangeId, DateTime lastCycleTime)
    {
        var mockCache = new Mock<IMarketDataCache>();
        mockCache.Setup(c => c.GetAllForExchange(It.IsAny<string>())).Returns(new List<FundingRateDto>());
        mockCache.Setup(c => c.IsStaleForExchange(It.IsAny<string>(), It.IsAny<TimeSpan>())).Returns(true);

        var sut = new FundingRateFetcher(
            _mockScopeFactory.Object, mockCache.Object, _mockReadinessSignal.Object,
            _mockHubContext.Object, NullLogger<FundingRateFetcher>.Instance);

        // Seed the internal settlement tracking state via reflection
        var field = typeof(FundingRateFetcher)
            .GetField("_lastCycleTimePerExchange", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>)field!.GetValue(sut)!;
        dict[exchangeId] = lastCycleTime;

        return sut;
    }
}
