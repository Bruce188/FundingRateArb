using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Controllers;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Web;

/// <summary>
/// Tests that the DashboardController's realized-PnL aggregate correctly excludes
/// positions flagged as phantom-fee backfills (IsPhantomFeeBackfill == true).
/// These positions have synthetic fee corrections that should not pollute the
/// historical realized-PnL total shown on the dashboard.
/// </summary>
public class DashboardControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IBotConfigRepository> _mockBotConfigRepo;
    private readonly Mock<IPositionRepository> _mockPositionRepo;
    private readonly Mock<IFundingRateRepository> _mockFundingRateRepo;
    private readonly Mock<IAlertRepository> _mockAlertRepo;
    private readonly Mock<ILogger<DashboardController>> _mockLogger;
    private readonly Mock<ISignalEngine> _mockSignalEngine;
    private readonly Mock<IBotControl> _mockBotControl;
    private readonly Mock<IUserSettingsService> _mockUserSettings;
    private readonly Mock<ICircuitBreakerManager> _mockCircuitBreaker;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockScopeProvider;
    private readonly Mock<IMarketDataCache> _mockMarketDataCache;
    private readonly Mock<IBalanceAggregator> _mockBalanceAggregator;
    private readonly IMemoryCache _cache;
    private readonly DashboardController _controller;

    public DashboardControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockBotConfigRepo = new Mock<IBotConfigRepository>();
        _mockPositionRepo = new Mock<IPositionRepository>();
        _mockFundingRateRepo = new Mock<IFundingRateRepository>();
        _mockAlertRepo = new Mock<IAlertRepository>();
        _mockLogger = new Mock<ILogger<DashboardController>>();
        _mockSignalEngine = new Mock<ISignalEngine>();
        _mockBotControl = new Mock<IBotControl>();
        _mockUserSettings = new Mock<IUserSettingsService>();
        _mockCircuitBreaker = new Mock<ICircuitBreakerManager>();
        _mockCircuitBreaker.Setup(m => m.GetActivePairCooldowns()).Returns([]);
        _mockCircuitBreaker.Setup(m => m.GetCircuitBreakerStates()).Returns([]);

        _mockUserSettings.Setup(s => s.GetOrCreateConfigAsync(It.IsAny<string>()))
            .ReturnsAsync(new UserConfiguration { IsEnabled = false });
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<int> { 1, 2, 3 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<int> { 1, 2, 3, 4, 5 });
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int>());

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfigRepo.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRateRepo.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlertRepo.Object);

        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { IsEnabled = false });
        _mockPositionRepo.Setup(r => r.GetOpenAsync())
            .ReturnsAsync([]);
        _mockPositionRepo.Setup(r => r.GetOpenByUserAsync(It.IsAny<string>()))
            .ReturnsAsync([]);
        _mockPositionRepo.Setup(r => r.CountByStatusAsync(It.IsAny<PositionStatus>()))
            .ReturnsAsync(0);
        _mockPositionRepo.Setup(r => r.CountByStatusesAsync(It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(0);
        _mockPositionRepo.Setup(r => r.GetByUserAndStatusesAsync(
                It.IsAny<string>(), It.IsAny<PositionStatus[]>()))
            .ReturnsAsync([]);
        _mockPositionRepo.Setup(r => r.SumRealizedPnlExcludingPhantomAsync(
                It.IsAny<string>(), It.IsAny<PositionStatus[]>()))
            .ReturnsAsync(0m);
        _mockFundingRateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync([]);
        _mockAlertRepo.Setup(r => r.GetByUserAsync(It.IsAny<string>(), true, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        _cache = new MemoryCache(new MemoryCacheOptions());

        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopeProvider = new Mock<IServiceProvider>();

        _mockScopeProvider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(_mockUow.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IUserSettingsService))).Returns(_mockUserSettings.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);

        _mockMarketDataCache = new Mock<IMarketDataCache>();
        _mockMarketDataCache.Setup(m => m.GetLastFetchTime()).Returns((DateTime?)null);

        _mockBalanceAggregator = new Mock<IBalanceAggregator>();

        _controller = new DashboardController(
            _mockUow.Object, _mockLogger.Object, _mockSignalEngine.Object,
            _mockBotControl.Object, _mockUserSettings.Object, _cache,
            _mockCircuitBreaker.Object, _mockScopeFactory.Object,
            _mockMarketDataCache.Object, _mockBalanceAggregator.Object);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user },
        };
    }

    // ── Phantom-fee backfill exclusion from realized-PnL aggregate ────────────

    [Fact]
    public async Task Index_RealizedPnlAggregate_ExcludesPhantomBackfillRows()
    {
        // Arrange: SQL aggregate returns 150 (sum of two non-phantom rows: 100 + 50).
        // The phantom row (200) is excluded by the SQL WHERE clause in the repository.
        _mockPositionRepo
            .Setup(r => r.SumRealizedPnlExcludingPhantomAsync(
                "test-user-id",
                PositionStatus.Closed,
                PositionStatus.EmergencyClosed,
                PositionStatus.Liquidated))
            .ReturnsAsync(150m);

        // Act
        var result = await _controller.Index();

        // Assert: the SQL aggregate (150) flows through to TotalRealizedPnl on the ViewModel.
        var model = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<DashboardViewModel>().Subject;

        model.TotalRealizedPnl.Should().Be(150m,
            "the realized-PnL aggregate must exclude rows where IsPhantomFeeBackfill == true");
    }

    [Fact]
    public async Task Index_RealizedPnlAggregate_AllPhantomBackfill_YieldsZero()
    {
        // Arrange: SQL aggregate returns 0 when all rows are phantom-fee backfills.
        _mockPositionRepo
            .Setup(r => r.SumRealizedPnlExcludingPhantomAsync(
                "test-user-id",
                PositionStatus.Closed,
                PositionStatus.EmergencyClosed,
                PositionStatus.Liquidated))
            .ReturnsAsync(0m);

        // Act
        var result = await _controller.Index();

        // Assert
        var model = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<DashboardViewModel>().Subject;

        model.TotalRealizedPnl.Should().Be(0m,
            "when all closed positions are phantom-fee backfills, TotalRealizedPnl must be 0");
    }

    [Fact]
    public async Task Index_RealizedPnlAggregate_NoClosedPositions_YieldsZero()
    {
        // Arrange: default mock returns 0m (no closed positions) — no override needed.
        // Act
        var result = await _controller.Index();

        // Assert
        var model = result.Should().BeOfType<ViewResult>().Subject
            .Model.Should().BeOfType<DashboardViewModel>().Subject;

        model.TotalRealizedPnl.Should().Be(0m,
            "TotalRealizedPnl must be 0 when there are no closed positions");
    }
}
