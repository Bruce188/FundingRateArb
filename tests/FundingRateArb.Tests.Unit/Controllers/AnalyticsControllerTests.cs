using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.Controllers;
using FundingRateArb.Web.ViewModels;

namespace FundingRateArb.Tests.Unit.Controllers;

public class AnalyticsControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<ITradeAnalyticsService> _mockTradeAnalytics;
    private readonly Mock<IRateAnalyticsService> _mockRateAnalytics;
    private readonly Mock<IUserSettingsService> _mockUserSettings;
    private readonly Mock<IAssetRepository> _mockAssetRepo;
    private readonly Mock<IExchangeRepository> _mockExchangeRepo;
    private readonly AnalyticsController _controller;

    public AnalyticsControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockTradeAnalytics = new Mock<ITradeAnalyticsService>();
        _mockRateAnalytics = new Mock<IRateAnalyticsService>();
        _mockUserSettings = new Mock<IUserSettingsService>();
        _mockAssetRepo = new Mock<IAssetRepository>();
        _mockExchangeRepo = new Mock<IExchangeRepository>();

        _mockUow.Setup(u => u.Assets).Returns(_mockAssetRepo.Object);
        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);

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
}
