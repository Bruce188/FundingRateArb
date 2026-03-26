using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Controllers;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class AnalyticsControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ITradeAnalyticsService> _mockTradeAnalytics;
    private readonly Mock<IRateAnalyticsService> _mockRateAnalytics;
    private readonly Mock<IUserSettingsService> _mockUserSettings;
    private readonly Mock<IAssetRepository> _mockAssetRepo;
    private readonly Mock<IExchangeRepository> _mockExchangeRepo;
    private readonly Mock<IPositionRepository> _mockPositionRepo;
    private readonly Mock<IOpportunitySnapshotRepository> _mockSnapshotRepo;
    private readonly AnalyticsController _controller;

    public AnalyticsControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockTradeAnalytics = new Mock<ITradeAnalyticsService>();
        _mockRateAnalytics = new Mock<IRateAnalyticsService>();
        _mockUserSettings = new Mock<IUserSettingsService>();
        _mockAssetRepo = new Mock<IAssetRepository>();
        _mockExchangeRepo = new Mock<IExchangeRepository>();
        _mockPositionRepo = new Mock<IPositionRepository>();
        _mockSnapshotRepo = new Mock<IOpportunitySnapshotRepository>();

        _mockUow.Setup(u => u.Assets).Returns(_mockAssetRepo.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockUow.Setup(u => u.OpportunitySnapshots).Returns(_mockSnapshotRepo.Object);

        _mockAssetRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(new List<Asset>
        {
            new() { Id = 1, Symbol = "ETH" },
            new() { Id = 2, Symbol = "BTC" },
        });

        _mockExchangeRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid" },
            new() { Id = 2, Name = "Lighter" },
        });

        // Default setup for SQL-aggregated KPI methods
        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto());
        _mockPositionRepo.Setup(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.AssetKpiAggregateDto>());
        _mockPositionRepo.Setup(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.ExchangePairKpiAggregateDto>());

        _controller = new AnalyticsController(_mockUow.Object, _mockTradeAnalytics.Object, _mockRateAnalytics.Object, _mockUserSettings.Object);
    }

    private void SetupAdminUser()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin-user-id"),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    private void SetupNormalUser(List<int>? assetIds = null, List<int>? exchangeIds = null)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync("test-user-id"))
            .ReturnsAsync(assetIds ?? new List<int> { 1 });
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync("test-user-id"))
            .ReturnsAsync(exchangeIds ?? new List<int> { 1, 2 });
    }

    // ── RateAnalytics View ──────────────────────────────────────

    [Fact]
    public async Task RateAnalytics_AdminUser_ReturnsViewResult()
    {
        SetupAdminUser();
        _mockRateAnalytics.Setup(s => s.GetRateTrendsAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RateTrendDto>());

        var result = await _controller.RateAnalytics(1, 7);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        viewResult.Model.Should().BeOfType<RateAnalyticsViewModel>();
    }

    // ── RateTrendData JSON — out-of-scope returns 403 ───────────

    [Fact]
    public async Task RateTrendData_OutOfScopeAsset_Returns403Json()
    {
        // User only has asset 1 enabled, requesting asset 2
        SetupNormalUser(assetIds: new List<int> { 1 });

        var result = await _controller.RateTrendData(assetId: 2, exchangeId: null, days: 7);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        jsonResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task RateTrendData_InScopeAsset_ReturnsData()
    {
        SetupNormalUser(assetIds: new List<int> { 1 });
        _mockRateAnalytics.Setup(s => s.GetRateTrendsAsync(1, 7, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RateTrendDto>
            {
                new(1, "ETH", 1, "Hyperliquid", 0.001m, 0.001m, 0.001m, "stable", new List<HourlyRatePoint>()),
            });

        var result = await _controller.RateTrendData(assetId: 1, exchangeId: null, days: 7);

        result.Should().BeOfType<JsonResult>();
        var jsonResult = (JsonResult)result;
        jsonResult.StatusCode.Should().BeNull(); // default 200
    }

    [Fact]
    public async Task RateTrendData_OutOfScopeExchange_Returns403Json()
    {
        // User only has exchange 1 enabled, requesting exchange 2
        SetupNormalUser(assetIds: new List<int> { 1 }, exchangeIds: new List<int> { 1 });

        var result = await _controller.RateTrendData(assetId: 1, exchangeId: 2, days: 7);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        jsonResult.StatusCode.Should().Be(403);
    }

    // ── Days clamping ───────────────────────────────────────────

    [Fact]
    public async Task RateAnalytics_DaysClampedTo30()
    {
        SetupAdminUser();
        _mockRateAnalytics.Setup(s => s.GetRateTrendsAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RateTrendDto>());

        var result = await _controller.RateAnalytics(1, days: 90);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<RateAnalyticsViewModel>().Subject;
        model.SelectedDays.Should().Be(30);
    }

    [Fact]
    public async Task RateAnalytics_DaysClampedToMinimum1()
    {
        SetupAdminUser();
        _mockRateAnalytics.Setup(s => s.GetRateTrendsAsync(It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RateTrendDto>());

        var result = await _controller.RateAnalytics(1, days: -5);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<RateAnalyticsViewModel>().Subject;
        model.SelectedDays.Should().Be(1);
    }

    // ── TimeOfDayData — scope check ─────────────────────────────

    [Fact]
    public async Task TimeOfDayData_OutOfScopeExchange_Returns403Json()
    {
        SetupNormalUser(assetIds: new List<int> { 1 }, exchangeIds: new List<int> { 1 });

        var result = await _controller.TimeOfDayData(assetId: 1, exchangeId: 2, days: 7);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        jsonResult.StatusCode.Should().Be(403);
    }

    // ── NB7: RateAnalytics view Forbid for out-of-scope asset ────

    [Fact]
    public async Task RateAnalytics_NormalUser_OutOfScopeAsset_ReturnsForbid()
    {
        // User only has asset 1 enabled, requesting asset 2
        SetupNormalUser(assetIds: new List<int> { 1 });

        var result = await _controller.RateAnalytics(assetId: 2, days: 7);

        result.Should().BeOfType<ForbidResult>();
    }

    // ── NB5: Correlation scope enforcement ───────────────────────

    [Fact]
    public async Task Correlation_OutOfScopeAsset_ReturnsForbid()
    {
        // User only has asset 1 enabled, requesting asset 2
        SetupNormalUser(assetIds: new List<int> { 1 });

        var result = await _controller.Correlation(assetId: 2, days: 7);

        result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Correlation_InScopeAsset_FiltersCorrelationsByUserExchangeScope()
    {
        // User has asset 1 and only exchange 1 (Hyperliquid) enabled
        SetupNormalUser(assetIds: new List<int> { 1 }, exchangeIds: new List<int> { 1 });
        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Asset { Id = 1, Symbol = "ETH" });
        _mockRateAnalytics.Setup(s => s.GetCrossExchangeCorrelationAsync(1, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CorrelationPairDto>
            {
                new("Hyperliquid", "Lighter", 0.95m, 100),   // Lighter is out of scope
                new("Hyperliquid", "Hyperliquid", 1.0m, 50), // both in scope (edge case)
            });

        var result = await _controller.Correlation(assetId: 1, days: 7);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<CorrelationViewModel>().Subject;
        // Only the pair where BOTH exchanges are in scope should remain
        model.Correlations.Should().HaveCount(1);
        model.Correlations[0].Exchange1.Should().Be("Hyperliquid");
        model.Correlations[0].Exchange2.Should().Be("Hyperliquid");
    }

    // ── NB6: ZScoreAlerts tests ─────────────────────────────────

    [Fact]
    public async Task ZScoreAlerts_AdminUser_ReturnsUnfilteredAlerts()
    {
        SetupAdminUser();
        var alerts = new List<ZScoreAlertDto>
        {
            new("ETH", "Hyperliquid", 0.005m, 0.001m, 0.001m, 4.0m),
            new("BTC", "Lighter", 0.003m, 0.001m, 0.0005m, 4.0m),
        };
        _mockRateAnalytics.Setup(s => s.GetZScoreAlertsAsync(2.0m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alerts);

        var result = await _controller.ZScoreAlerts(threshold: 2.0m);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var data = jsonResult.Value as List<ZScoreAlertDto>;
        data.Should().HaveCount(2);
    }

    [Fact]
    public async Task ZScoreAlerts_NormalUser_FiltersAlertsByScope()
    {
        // User only has asset 1 (ETH) and exchange 1 (Hyperliquid) enabled
        SetupNormalUser(assetIds: new List<int> { 1 }, exchangeIds: new List<int> { 1 });
        var alerts = new List<ZScoreAlertDto>
        {
            new("ETH", "Hyperliquid", 0.005m, 0.001m, 0.001m, 4.0m),  // in scope
            new("ETH", "Lighter", 0.004m, 0.001m, 0.001m, 3.0m),      // exchange out of scope
            new("BTC", "Hyperliquid", 0.003m, 0.001m, 0.0005m, 4.0m), // asset out of scope
        };
        _mockRateAnalytics.Setup(s => s.GetZScoreAlertsAsync(2.0m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(alerts);

        var result = await _controller.ZScoreAlerts(threshold: 2.0m);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var data = jsonResult.Value as List<ZScoreAlertDto>;
        data.Should().HaveCount(1);
        data![0].AssetSymbol.Should().Be("ETH");
        data[0].ExchangeName.Should().Be("Hyperliquid");
    }

    [Fact]
    public async Task ZScoreAlerts_ThresholdClamped_ToMaximum10()
    {
        SetupAdminUser();
        _mockRateAnalytics.Setup(s => s.GetZScoreAlertsAsync(10.0m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ZScoreAlertDto>());

        var result = await _controller.ZScoreAlerts(threshold: 50.0m);

        // Verify the service was called with clamped threshold of 10.0
        _mockRateAnalytics.Verify(s => s.GetZScoreAlertsAsync(10.0m, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Index KPI tests (NB3) ───────────────────────────────────

    [Fact]
    public async Task Index_WithClosedPositions_PopulatesKPIs()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        // SQL-aggregated scalar KPIs
        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto
            {
                TotalTrades = 3,
                WinCount = 2,
                TotalPnl = 25m,
                Pnl7d = 25m,
                Pnl30d = 25m,
                BestPnl = 20m,
                WorstPnl = -5m,
                TotalHoldHours = 7.0,  // 2h + 4h + 1h
            });

        // SQL-aggregated per-asset KPIs
        _mockPositionRepo.Setup(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.AssetKpiAggregateDto>
            {
                new() { AssetSymbol = "ETH", Trades = 2, WinCount = 1, TotalPnl = 5m },
                new() { AssetSymbol = "BTC", Trades = 1, WinCount = 1, TotalPnl = 20m },
            });

        // SQL-aggregated per-exchange KPIs
        _mockPositionRepo.Setup(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.ExchangePairKpiAggregateDto>
            {
                new() { LongExchangeName = "Hyperliquid", ShortExchangeName = "Lighter", Trades = 2, WinCount = 1, TotalPnl = 5m },
                new() { LongExchangeName = "Lighter", ShortExchangeName = "Hyperliquid", Trades = 1, WinCount = 1, TotalPnl = 20m },
            });

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        vm.TotalTrades.Should().Be(3);
        vm.TotalRealizedPnl.Should().Be(25m);
        vm.WinRate.Should().BeApproximately(2m / 3m, 0.01m);
        vm.BestTradePnl.Should().Be(20m);
        vm.WorstTradePnl.Should().Be(-5m);
        vm.PerAsset.Should().HaveCount(2);
        vm.PerExchangePair.Should().HaveCount(2);

        // NB4: Assert specific PerAsset AvgPnl and WinRate values
        // ETH: PnL = 5, trades = 2, wins = 1 -> AvgPnl = 2.5, WinRate = 0.5
        var eth = vm.PerAsset.First(a => a.AssetSymbol == "ETH");
        eth.TotalPnl.Should().Be(5m);
        eth.Trades.Should().Be(2);
        eth.AvgPnl.Should().Be(2.5m);
        eth.WinRate.Should().Be(0.5m);

        // BTC: PnL = 20, trades = 1, wins = 1 -> AvgPnl = 20, WinRate = 1.0
        var btc = vm.PerAsset.First(a => a.AssetSymbol == "BTC");
        btc.TotalPnl.Should().Be(20m);
        btc.Trades.Should().Be(1);
        btc.AvgPnl.Should().Be(20m);
        btc.WinRate.Should().Be(1.0m);
    }

    [Fact]
    public async Task Index_WithNoClosedPositions_ReturnsZeroKPIs()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());
        // Default KPI aggregate returns zeros (set up in constructor)

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        vm.TotalTrades.Should().Be(0);
        vm.TotalRealizedPnl.Should().Be(0);
        vm.WinRate.Should().Be(0);
        vm.AvgHoldTimeHours.Should().Be(0);
        vm.AvgPnlPerTrade.Should().Be(0);
        vm.BestTradePnl.Should().Be(0);
        vm.WorstTradePnl.Should().Be(0);
        vm.PerAsset.Should().BeEmpty();
        vm.PerExchangePair.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_AdminUser_SeesAllUsersPositions()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        await _controller.Index();

        // Verify _tradeAnalytics is called with correct parameters
        _mockTradeAnalytics.Verify(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()), Times.Once);
        // effectiveUserId should be null for admin (passes through to all SQL aggregate queries)
        _mockPositionRepo.Verify(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _mockPositionRepo.Verify(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _mockPositionRepo.Verify(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Index_NormalUser_SeesOnlyOwnPositions()
    {
        SetupNormalUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync("test-user-id", 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());
        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), "test-user-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto());
        _mockPositionRepo.Setup(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), "test-user-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.AssetKpiAggregateDto>());
        _mockPositionRepo.Setup(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), "test-user-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.ExchangePairKpiAggregateDto>());

        await _controller.Index();

        // Verify _tradeAnalytics is called with the user's ID
        _mockTradeAnalytics.Verify(s => s.GetAllPositionAnalyticsAsync("test-user-id", 0, 50, It.IsAny<CancellationToken>()), Times.Once);
        // effectiveUserId should be the user's ID for non-admin
        _mockPositionRepo.Verify(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), "test-user-id", It.IsAny<CancellationToken>()), Times.Once);
        _mockPositionRepo.Verify(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), "test-user-id", It.IsAny<CancellationToken>()), Times.Once);
        _mockPositionRepo.Verify(r => r.GetPerExchangePairKpiAsync(It.IsAny<DateTime>(), "test-user-id", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── PassedOpportunities tests (NB4) ─────────────────────────

    // ── NB6: 7d/30d PnL window filtering ───────────────────────

    [Fact]
    public async Task Index_PnlWindowFiltering_ReflectsSqlAggregateWindows()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        // SQL-aggregated KPIs with pre-computed 7d/30d windows
        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto
            {
                TotalTrades = 3,
                WinCount = 3,
                TotalPnl = 80m,
                Pnl7d = 10m,     // only the 2-day-old position
                Pnl30d = 30m,    // 2-day + 15-day positions
                BestPnl = 50m,
                WorstPnl = 10m,
                TotalHoldHours = 6.0,
            });

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        vm.TotalRealizedPnl7d.Should().Be(10m);
        vm.TotalRealizedPnl30d.Should().Be(30m);
        vm.TotalRealizedPnl.Should().Be(80m);
    }

    // ── PassedOpportunities tests (NB4) ─────────────────────────

    [Fact]
    public async Task PassedOpportunities_PopulatesSkipReasonStats()
    {
        SetupAdminUser();
        _mockSnapshotRepo.Setup(r => r.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpportunitySnapshot>());
        _mockSnapshotRepo.Setup(r => r.GetSkipReasonStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((10, 3, new Dictionary<string, int>
            {
                { "capital_exhausted", 4 },
                { "cooldown", 2 },
                { "below_threshold", 1 },
            }));

        var result = await _controller.PassedOpportunities();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PassedOpportunitiesViewModel>().Subject;

        vm.TotalOpportunitiesSeen.Should().Be(10);
        vm.TotalOpened.Should().Be(3);
        vm.OpenedPct.Should().Be(30m);
        vm.TopSkipReason.Should().Be("capital_exhausted");
        vm.SkipReasons.Should().HaveCount(3);
        vm.SkipReasons[0].Reason.Should().Be("capital_exhausted");
        vm.SkipReasons[0].Count.Should().Be(4);
    }

    [Fact]
    public async Task PassedOpportunities_ZeroOpportunities_OpenedPctIsZero()
    {
        SetupAdminUser();
        _mockSnapshotRepo.Setup(r => r.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpportunitySnapshot>());
        _mockSnapshotRepo.Setup(r => r.GetSkipReasonStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0, new Dictionary<string, int>()));

        var result = await _controller.PassedOpportunities();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PassedOpportunitiesViewModel>().Subject;

        vm.TotalOpportunitiesSeen.Should().Be(0);
        vm.OpenedPct.Should().Be(0);
        vm.TopSkipReason.Should().Be("N/A");
        vm.SkipReasons.Should().BeEmpty();
    }

    [Fact]
    public async Task PassedOpportunities_TopSkipReason_UsesHighestCount()
    {
        SetupAdminUser();
        _mockSnapshotRepo.Setup(r => r.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpportunitySnapshot>());
        _mockSnapshotRepo.Setup(r => r.GetSkipReasonStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((20, 5, new Dictionary<string, int>
            {
                { "below_threshold", 3 },
                { "cooldown", 10 },
                { "capital_exhausted", 2 },
            }));

        var result = await _controller.PassedOpportunities();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PassedOpportunitiesViewModel>().Subject;

        // Should be ordered by count descending, so cooldown is the top
        vm.TopSkipReason.Should().Be("cooldown");
        vm.SkipReasons[0].Reason.Should().Be("cooldown");
    }

    // ── NB5: Parameter clamping tests ───────────────────────────

    [Theory]
    [InlineData(0, 1)]      // days=0 → clamps to 1
    [InlineData(-5, 1)]     // negative → clamps to 1
    [InlineData(500, 365)]  // over max → clamps to 365
    [InlineData(90, 90)]    // within range → unchanged
    public async Task Index_DaysParameterClamped(int inputDays, int expectedDays)
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        await _controller.Index(days: inputDays);

        // Verify the since date passed to the repository corresponds to the clamped days value
        _mockPositionRepo.Verify(r => r.GetKpiAggregatesAsync(
            It.Is<DateTime>(d => d >= DateTime.UtcNow.AddDays(-expectedDays - 1) && d <= DateTime.UtcNow.AddDays(-expectedDays + 1)),
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-5, 0)]     // negative skip → clamps to 0
    [InlineData(0, 0)]      // zero skip → unchanged
    [InlineData(10, 10)]    // positive → unchanged
    public async Task Index_SkipParameterClamped(int inputSkip, int expectedSkip)
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, expectedSkip, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        await _controller.Index(skip: inputSkip);

        _mockTradeAnalytics.Verify(s => s.GetAllPositionAnalyticsAsync(null, expectedSkip, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 1)]      // take=0 → clamps to 1
    [InlineData(-1, 1)]     // negative → clamps to 1
    [InlineData(999, 200)]  // over max → clamps to 200
    [InlineData(50, 50)]    // within range → unchanged
    public async Task Index_TakeParameterClamped(int inputTake, int expectedTake)
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, expectedTake, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        await _controller.Index(take: inputTake);

        _mockTradeAnalytics.Verify(s => s.GetAllPositionAnalyticsAsync(null, 0, expectedTake, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 1)]      // days=0 → clamps to 1
    [InlineData(-1, 1)]     // negative → clamps to 1
    [InlineData(50, 30)]    // over max → clamps to 30
    [InlineData(15, 15)]    // within range → unchanged
    public async Task PassedOpportunities_DaysParameterClamped(int inputDays, int expectedDays)
    {
        SetupAdminUser();
        _mockSnapshotRepo.Setup(r => r.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpportunitySnapshot>());
        _mockSnapshotRepo.Setup(r => r.GetSkipReasonStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0, new Dictionary<string, int>()));

        await _controller.PassedOpportunities(days: inputDays);

        // Verify the from date passed to the repository corresponds to the clamped days value
        _mockSnapshotRepo.Verify(r => r.GetRecentAsync(
            It.Is<DateTime>(d => d >= DateTime.UtcNow.AddDays(-expectedDays - 1) && d <= DateTime.UtcNow.AddDays(-expectedDays + 1)),
            It.IsAny<DateTime>(), 0, 100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0, 1)]      // take=0 → clamps to 1
    [InlineData(999, 200)]  // over max → clamps to 200
    public async Task PassedOpportunities_TakeParameterClamped(int inputTake, int expectedTake)
    {
        SetupAdminUser();
        _mockSnapshotRepo.Setup(r => r.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, expectedTake, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OpportunitySnapshot>());
        _mockSnapshotRepo.Setup(r => r.GetSkipReasonStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0, new Dictionary<string, int>()));

        await _controller.PassedOpportunities(take: inputTake);

        _mockSnapshotRepo.Verify(r => r.GetRecentAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, expectedTake, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── N4: Null RealizedPnl exclusion ──────────────────────────

    [Fact]
    public async Task Index_NullRealizedPnl_ExcludedFromKPIs()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        // SQL aggregate already excludes null PnL (WHERE RealizedPnl IS NOT NULL)
        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto
            {
                TotalTrades = 1,
                WinCount = 1,
                TotalPnl = 10m,
                Pnl7d = 10m,
                Pnl30d = 10m,
                BestPnl = 10m,
                WorstPnl = 10m,
                TotalHoldHours = 2.0,
            });

        _mockPositionRepo.Setup(r => r.GetPerAssetKpiAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Application.DTOs.AssetKpiAggregateDto>
            {
                new() { AssetSymbol = "ETH", Trades = 1, WinCount = 1, TotalPnl = 10m },
            });

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        // Only 1 position has PnL — the null one is excluded by SQL
        vm.TotalTrades.Should().Be(1);
        vm.TotalRealizedPnl.Should().Be(10m);
        vm.WinRate.Should().Be(1.0m);
        // Per-asset should only include ETH (the one with PnL)
        vm.PerAsset.Should().HaveCount(1);
        vm.PerAsset[0].AssetSymbol.Should().Be("ETH");
    }

    // ── B1: OpportunitiesIndex sequential query execution ────────

    [Fact]
    public async Task PassedOpportunities_QueriesExecuteSequentially()
    {
        SetupAdminUser();
        var callOrder = new List<string>();

        _mockSnapshotRepo.Setup(r => r.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 0, 100, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("GetRecentAsync"))
            .ReturnsAsync(new List<OpportunitySnapshot>());
        _mockSnapshotRepo.Setup(r => r.GetSkipReasonStatsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("GetSkipReasonStatsAsync"))
            .ReturnsAsync((0, 0, new Dictionary<string, int>()));

        await _controller.PassedOpportunities();

        // Both queries must have been called in order (sequential execution)
        callOrder.Should().ContainInOrder("GetRecentAsync", "GetSkipReasonStatsAsync");
    }

    // ── NB1: HoldDataCount=0 produces zero avg ───────────────────

    [Fact]
    public async Task Index_HoldDataCountZero_AvgHoldTimeIsZero()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto
            {
                TotalTrades = 5,
                WinCount = 3,
                TotalPnl = 50m,
                Pnl7d = 50m,
                Pnl30d = 50m,
                BestPnl = 30m,
                WorstPnl = -5m,
                TotalHoldHours = 0.0,
                HoldDataCount = 0,
            });

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        vm.TotalTrades.Should().Be(5);
        vm.AvgHoldTimeHours.Should().Be(0m);
    }

    // ── NB2-v40: Capped HoldDataCount denominator ─────────────────

    [Fact]
    public async Task Index_AvgHoldTime_UsesCappedDenominator()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto
            {
                TotalTrades = 15000,
                WinCount = 10000,
                TotalPnl = 100m,
                Pnl7d = 50m,
                Pnl30d = 100m,
                BestPnl = 30m,
                WorstPnl = -5m,
                TotalHoldHours = 50000.0,
                HoldDataCount = 10000,
            });

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        // AvgHoldTimeHours = 50000.0 / 10000 = 5.0 (uses HoldDataCount, not TotalTrades)
        vm.AvgHoldTimeHours.Should().Be(5m);
    }

    // ── NB7: ClosedAt=null edge case ─────────────────────────────

    [Fact]
    public async Task Index_ClosedAtNull_UsesZeroFallbackForHoldTime()
    {
        SetupAdminUser();
        _mockTradeAnalytics.Setup(s => s.GetAllPositionAnalyticsAsync(null, 0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PositionAnalyticsSummaryDto>());

        // SQL aggregate handles ClosedAt=null with CASE WHEN fallback to 0
        // Position 1: null ClosedAt -> 0 hours, Position 2: 4 hours -> total 4h, avg = 2h
        _mockPositionRepo.Setup(r => r.GetKpiAggregatesAsync(It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Application.DTOs.KpiAggregateDto
            {
                TotalTrades = 2,
                WinCount = 2,
                TotalPnl = 30m,
                Pnl7d = 30m,
                Pnl30d = 30m,
                BestPnl = 20m,
                WorstPnl = 10m,
                TotalHoldHours = 4.0,  // 0h (null ClosedAt) + 4h = 4h total
                HoldDataCount = 2,
            });

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionAnalyticsIndexViewModel>().Subject;

        vm.TotalTrades.Should().Be(2);
        // AvgHoldTimeHours: (0 + 4) / 2 = 2
        vm.AvgHoldTimeHours.Should().Be(2m);
    }
}
