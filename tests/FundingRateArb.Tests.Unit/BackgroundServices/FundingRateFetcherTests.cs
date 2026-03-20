using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Hubs;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using FundingRateArb.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using FundingRateArb.Domain.Enums;

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

        _sut = new FundingRateFetcher(
            _mockScopeFactory.Object,
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
            d => d.ReceiveOpportunityUpdate(It.IsAny<List<ArbitrageOpportunityDto>>()),
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
}
