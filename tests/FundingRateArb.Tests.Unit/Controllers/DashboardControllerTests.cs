using System.Security.Claims;
using FluentAssertions;
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
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

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
        _mockFundingRateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync([]);
        _mockAlertRepo.Setup(r => r.GetByUserAsync(It.IsAny<string>(), true, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FundingRateArb.Application.DTOs.OpportunityResultDto());

        _cache = new MemoryCache(new MemoryCacheOptions());

        _controller = new DashboardController(_mockUow.Object, _mockLogger.Object, _mockSignalEngine.Object, _mockBotControl.Object, _mockUserSettings.Object, _cache);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
    }

    [Fact]
    public async Task Index_ReturnsViewResult_WithDashboardViewModel()
    {
        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        viewResult.Model.Should().BeOfType<DashboardViewModel>();
    }

    [Fact]
    public async Task Index_PopulatesOpenPositionCount_FromRepository()
    {
        // Arrange
        var positions = new List<ArbitragePosition>
        {
            new ArbitragePosition
            {
                Id = 1, UserId = "test-user-id", Status = PositionStatus.Open,
                SizeUsdc = 100m, CurrentSpreadPerHour = 0.001m
            },
            new ArbitragePosition
            {
                Id = 2, UserId = "test-user-id", Status = PositionStatus.Open,
                SizeUsdc = 150m, CurrentSpreadPerHour = 0.002m
            }
        };
        _mockPositionRepo.Setup(r => r.GetOpenByUserAsync("test-user-id")).ReturnsAsync(positions);

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.OpenPositionCount.Should().Be(2);
    }

    [Fact]
    public async Task Index_PopulatesBotEnabled_FromBotConfig()
    {
        // Arrange
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { IsEnabled = true });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.BotEnabled.Should().BeTrue();
    }

    [Fact]
    public void RetryNow_ReturnsJsonResult_NotRedirect()
    {
        // Arrange — give the controller an Admin role
        var adminUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin-user"),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = adminUser }
        };

        // Act
        var result = _controller.RetryNow();

        // Assert
        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var value = jsonResult.Value;
        value.Should().NotBeNull();

        var successProp = value!.GetType().GetProperty("success");
        successProp.Should().NotBeNull();
        successProp!.GetValue(value).Should().Be(true);
    }

    [Fact]
    public void RetryNow_CallsClearCooldownsAndTriggerImmediateCycle()
    {
        // Arrange
        var adminUser = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin-user"),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "mock"));
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = adminUser }
        };

        // Act
        _controller.RetryNow();

        // Assert
        _mockBotControl.Verify(b => b.ClearCooldowns(), Times.Once);
        _mockBotControl.Verify(b => b.TriggerImmediateCycle(), Times.Once);
    }

    [Fact]
    public async Task Index_WhenNoOpportunities_BestSpreadFallsToDiagnosticRawSpread()
    {
        // Arrange
        var diagnostics = new PipelineDiagnosticsDto
        {
            TotalRatesLoaded = 50,
            RatesAfterStalenessFilter = 45,
            TotalPairsEvaluated = 32,
            PairsFilteredByThreshold = 32,
            PairsPassing = 0,
            BestRawSpread = 0.000926m,
            OpenThreshold = 0.005m,
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = [],
                Diagnostics = diagnostics,
            });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.BestSpread.Should().Be(0.000926m);
        model.Diagnostics.Should().NotBeNull();
        model.Diagnostics!.BestRawSpread.Should().Be(0.000926m);
    }

    // ── NB12: PnL Progress Computation ──────────────────────────

    [Fact]
    public async Task Index_AdaptiveHoldDisabled_PnlProgressEmpty()
    {
        // Arrange
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { AdaptiveHoldEnabled = false });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.PnlProgressByPosition.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_ZeroFeeExchanges_PositionSkippedInPnlProgress()
    {
        // Arrange — both exchanges are Lighter (fee = 0), so target = 0, division skipped
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { AdaptiveHoldEnabled = true, TargetPnlMultiplier = 2.0m });
        _mockPositionRepo.Setup(r => r.GetOpenByUserAsync("test-user-id"))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new()
                {
                    Id = 1, UserId = "test-user-id", Status = PositionStatus.Open,
                    SizeUsdc = 100m, Leverage = 5, CurrentSpreadPerHour = 0.001m,
                    AccumulatedFunding = 0.5m,
                    LongExchange = new Exchange { Id = 2, Name = "Lighter" },
                    ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.PnlProgressByPosition.Should().BeEmpty();
    }

    [Fact]
    public async Task Index_NormalCase_PnlProgressComputedCorrectly()
    {
        // Arrange
        // Hyperliquid fee = 0.00045, Lighter fee = 0.0
        // entryFee = 100 * 5 * 2 * (0.00045 + 0.0) = 0.45
        // target = 2.0 * 0.45 = 0.9
        // progress = 0.45 / 0.9 = 0.5
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { AdaptiveHoldEnabled = true, TargetPnlMultiplier = 2.0m });
        _mockPositionRepo.Setup(r => r.GetOpenByUserAsync("test-user-id"))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new()
                {
                    Id = 1, UserId = "test-user-id", Status = PositionStatus.Open,
                    SizeUsdc = 100m, Leverage = 5, CurrentSpreadPerHour = 0.001m,
                    AccumulatedFunding = 0.45m,
                    LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
                    ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.PnlProgressByPosition.Should().ContainKey(1);
        model.PnlProgressByPosition[1].Should().BeApproximately(0.5m, 0.01m);
    }

    [Fact]
    public async Task Index_ZeroAccumulatedFunding_PositionExcludedFromPnlProgress()
    {
        // Arrange — AdaptiveHoldEnabled but position has zero accumulated funding
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { AdaptiveHoldEnabled = true, TargetPnlMultiplier = 2.0m });
        _mockPositionRepo.Setup(r => r.GetOpenByUserAsync("test-user-id"))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new()
                {
                    Id = 1, UserId = "test-user-id", Status = PositionStatus.Open,
                    SizeUsdc = 100m, Leverage = 5, CurrentSpreadPerHour = 0.001m,
                    AccumulatedFunding = 0m, // zero funding → should be excluded
                    LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
                    ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.PnlProgressByPosition.Should().BeEmpty("position with zero accumulated funding should be excluded");
    }

    [Fact]
    public async Task Index_LargeAccumulatedFunding_PnlProgressCappedAt2()
    {
        // Arrange — accumulated funding far exceeds target, cap should apply
        // entryFee = 100 * 5 * 2 * (0.00045 + 0.0) = 0.45
        // target = 2.0 * 0.45 = 0.9
        // raw progress = 10.0 / 0.9 ≈ 11.1 → capped to 2.0
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { AdaptiveHoldEnabled = true, TargetPnlMultiplier = 2.0m });
        _mockPositionRepo.Setup(r => r.GetOpenByUserAsync("test-user-id"))
            .ReturnsAsync(new List<ArbitragePosition>
            {
                new()
                {
                    Id = 1, UserId = "test-user-id", Status = PositionStatus.Open,
                    SizeUsdc = 100m, Leverage = 5, CurrentSpreadPerHour = 0.001m,
                    AccumulatedFunding = 10.0m,
                    LongExchange = new Exchange { Id = 1, Name = "Hyperliquid" },
                    ShortExchange = new Exchange { Id = 2, Name = "Lighter" },
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.PnlProgressByPosition.Should().ContainKey(1);
        model.PnlProgressByPosition[1].Should().Be(2.0m);
    }

    // ── Data-only exchange (CoinGlass) visibility ──────────────────────

    [Fact]
    public async Task Index_CoinGlassOpportunity_ShownWithoutPreference()
    {
        // Arrange — user has exchanges 1 and 2 enabled; CoinGlass (id=4) is data-only
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<int> { 1, 2 });
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int> { 4 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<int> { 10 });

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>
                {
                    new()
                    {
                        AssetId = 10,
                        LongExchangeId = 1,  // user-enabled
                        ShortExchangeId = 4, // CoinGlass (data-only, NOT in user prefs)
                        SpreadPerHour = 0.01m,
                    }
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert — opportunity should be visible because data-only exchanges are auto-included
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.Opportunities.Should().HaveCount(1);
    }

    [Fact]
    public async Task Index_CoinGlassOpportunity_NotFilteredByExchangePrefs()
    {
        // Arrange — user has only exchange 1 enabled; CoinGlass (id=4) is data-only
        // Opportunity has both exchanges as CoinGlass-related
        _mockUserSettings.Setup(s => s.GetUserEnabledExchangeIdsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<int> { 1 });
        _mockUserSettings.Setup(s => s.GetDataOnlyExchangeIdsAsync())
            .ReturnsAsync(new List<int> { 4 });
        _mockUserSettings.Setup(s => s.GetUserEnabledAssetIdsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<int> { 10 });

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>
                {
                    // This opportunity has exchange 3 which is NOT in user prefs and NOT data-only
                    new()
                    {
                        AssetId = 10,
                        LongExchangeId = 1,
                        ShortExchangeId = 3, // not enabled, not data-only
                        SpreadPerHour = 0.01m,
                    }
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert — opportunity should be filtered out because exchange 3 is not in user prefs
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.Opportunities.Should().BeEmpty();
    }

    // ── Anonymous dashboard access ─────────────────────────────────

    [Fact]
    public async Task Index_AnonymousUser_ReturnsViewWithOpportunities()
    {
        // Arrange — unauthenticated user (no claims)
        var anonUser = new ClaimsPrincipal(new ClaimsIdentity());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = anonUser }
        };

        var opportunities = new List<ArbitrageOpportunityDto>
        {
            new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SpreadPerHour = 0.005m }
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = opportunities });

        // Act
        var result = await _controller.Index();

        // Assert — returns view (not redirect/unauthorized) with opportunities
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.Opportunities.Should().HaveCount(1);
        model.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task Index_AnonymousUser_NoUserSpecificData()
    {
        // Arrange — unauthenticated user
        var anonUser = new ClaimsPrincipal(new ClaimsIdentity());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = anonUser }
        };

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        var result = await _controller.Index();

        // Assert — no user-specific data
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.OpenPositions.Should().BeEmpty();
        model.TotalUnreadAlerts.Should().Be(0);
        model.OpenPositionCount.Should().Be(0);
    }

    [Fact]
    public async Task Index_AuthenticatedUser_ReturnsFullDashboard()
    {
        // Arrange — authenticated user (from constructor setup)
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>
                {
                    new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SpreadPerHour = 0.005m }
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert — returns view with IsAuthenticated = true
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.IsAuthenticated.Should().BeTrue();
    }

    // ── Defense-in-depth: class-level [Authorize] attribute ─────────────────

    [Fact]
    public void DashboardController_HasClassLevelAuthorizeAttribute()
    {
        // B1: class-level [Authorize] ensures new actions default to authenticated
        var authorizeAttr = typeof(DashboardController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: false);
        authorizeAttr.Should().NotBeEmpty(
            "DashboardController must have a class-level [Authorize] attribute for defense-in-depth");
    }

    // ── B1: null-safe Diagnostics access on anonymous path ─────────────────

    [Fact]
    public async Task Index_AnonymousUser_WhenDiagnosticsNull_DoesNotThrow()
    {
        // Arrange — unauthenticated user with null Diagnostics
        var anonUser = new ClaimsPrincipal(new ClaimsIdentity());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = anonUser }
        };

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>(),
                Diagnostics = null,
            });

        // Act — should not throw NullReferenceException
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.BestSpread.Should().Be(0m);
    }

    [Fact]
    public async Task Index_AuthenticatedUser_WhenDiagnosticsNull_DoesNotThrow()
    {
        // Arrange — authenticated user (from constructor), null Diagnostics, no opportunities or positions
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Opportunities = new List<ArbitrageOpportunityDto>(),
                Diagnostics = null,
            });

        // Act — should not throw NullReferenceException
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.BestSpread.Should().Be(0m);
    }

    // ── Anonymous user: diagnostics not exposed ────────────────────────────

    [Fact]
    public async Task Index_AnonymousUser_DiagnosticsIsNull()
    {
        // Arrange — unauthenticated user
        var anonUser = new ClaimsPrincipal(new ClaimsIdentity());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = anonUser }
        };

        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto
            {
                Diagnostics = new PipelineDiagnosticsDto
                {
                    BestRawSpread = 0.005m,
                    OpenThreshold = 0.01m,
                    TotalRatesLoaded = 100,
                }
            });

        // Act
        var result = await _controller.Index();

        // Assert — NB1: diagnostics must not be exposed to anonymous users
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.Diagnostics.Should().BeNull(
            "anonymous users must not see pipeline diagnostics containing strategy parameters");
    }

    // ── NB2: BotEnabled hardcoded to false for anonymous ─────────────────

    [Fact]
    public async Task Index_AnonymousUser_BotEnabledIsFalse()
    {
        // Arrange — unauthenticated user with bot enabled globally
        var anonUser = new ClaimsPrincipal(new ClaimsIdentity());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = anonUser }
        };

        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { IsEnabled = true });
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        var result = await _controller.Index();

        // Assert — BotEnabled must be false regardless of global config
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.BotEnabled.Should().BeFalse("anonymous users must never see bot status");
    }

    // ── NB1: Anonymous path caches opportunity result ────────────────────

    [Fact]
    public async Task Index_AnonymousUser_SecondCall_UsesCachedOpportunities()
    {
        // Arrange — unauthenticated user
        var anonUser = new ClaimsPrincipal(new ClaimsIdentity());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = anonUser }
        };

        var opportunities = new List<ArbitrageOpportunityDto>
        {
            new() { AssetId = 1, LongExchangeId = 1, ShortExchangeId = 2, SpreadPerHour = 0.005m }
        };
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto { Opportunities = opportunities });

        // Act — call twice
        await _controller.Index();
        await _controller.Index();

        // Assert — signal engine should only be called once (second call uses cache)
        _mockSignalEngine.Verify(
            s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── NB3: DashboardController initial page load counts ────────────────

    [Fact]
    public async Task Index_NonZeroOpeningAndEmergencyClosed_PopulatesViewModelCounts()
    {
        // Arrange — CountByStatusAsync returns nonzero values
        _mockPositionRepo.Setup(r => r.CountByStatusAsync(PositionStatus.Opening))
            .ReturnsAsync(3);
        _mockPositionRepo.Setup(r => r.CountByStatusAsync(PositionStatus.EmergencyClosed))
            .ReturnsAsync(2);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<DashboardViewModel>().Subject;
        model.OpeningPositionCount.Should().Be(3);
        model.NeedsAttentionCount.Should().Be(2);
    }

    // ── NB4: Non-admin uses GetOpenByUserAsync ──────────────────────────

    [Fact]
    public async Task Index_AuthenticatedNonAdmin_UsesGetOpenByUserAsync()
    {
        // Arrange — authenticated non-admin user (from constructor setup)
        _mockSignalEngine.Setup(s => s.GetOpportunitiesWithDiagnosticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OpportunityResultDto());

        // Act
        var result = await _controller.Index();

        // Assert — should call GetOpenByUserAsync, not GetOpenAsync
        _mockPositionRepo.Verify(r => r.GetOpenByUserAsync("test-user-id"), Times.Once);
        _mockPositionRepo.Verify(r => r.GetOpenAsync(), Times.Never);
    }
}
