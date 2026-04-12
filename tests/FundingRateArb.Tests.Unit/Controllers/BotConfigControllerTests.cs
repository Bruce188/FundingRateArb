using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Areas.Admin.Controllers;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class BotConfigControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IBotConfigRepository> _mockBotConfigRepo;
    private readonly Mock<IConfigValidator> _mockValidator;
    private readonly BotConfigController _controller;

    public BotConfigControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockBotConfigRepo = new Mock<IBotConfigRepository>();
        _mockValidator = new Mock<IConfigValidator>();

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfigRepo.Object);

        _controller = new BotConfigController(
            _mockUow.Object, _mockValidator.Object, Mock.Of<ILogger<BotConfigController>>());

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin-user"),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "mock"));

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        _controller.TempData = new TempDataDictionary(
            _controller.ControllerContext.HttpContext,
            Mock.Of<ITempDataProvider>());
    }

    [Fact]
    public async Task Index_Post_ValidationFailure_ReturnsViewWithErrors()
    {
        // Arrange
        var config = new BotConfiguration
        {
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0005m, // invalid: Alert > Open
            CloseThreshold = 0.0001m,
            FeeAmortizationHours = 24,
            MaxHoldTimeHours = 72,
            DefaultLeverage = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            MaxConcurrentPositions = 1,
            MinPositionSizeUsdc = 10m,
            DailyDrawdownPausePct = 0.05m,
        };

        _mockBotConfigRepo.Setup(r => r.GetActiveTrackedAsync()).ReturnsAsync(config);
        _mockValidator.Setup(v => v.Validate(It.IsAny<BotConfiguration>()))
            .Returns(new ConfigValidationResult(false, new List<string>
            {
                "OpenThreshold must be greater than AlertThreshold."
            }));

        var model = new BotConfigViewModel
        {
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0005m,
            CloseThreshold = 0.0001m,
            TotalCapitalUsdc = 107m,
            DefaultLeverage = 5,
            MaxConcurrentPositions = 1,
            MaxCapitalPerPosition = 0.8m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            VolumeFraction = 0.001m,
            BreakevenHoursMax = 6,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            FeeAmortizationHours = 24,
            MinPositionSizeUsdc = 10m,
            MinVolume24hUsdc = 50_000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
        };

        // Act
        var result = await _controller.Index(model);

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        _controller.ModelState.IsValid.Should().BeFalse();
        _controller.ModelState[string.Empty]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("OpenThreshold");
    }

    [Fact]
    public async Task Index_Post_ValidConfig_RedirectsWithSuccess()
    {
        // Arrange
        var config = new BotConfiguration
        {
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0001m,
            CloseThreshold = 0.00005m,
            FeeAmortizationHours = 24,
            MaxHoldTimeHours = 72,
            DefaultLeverage = 5,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            MaxConcurrentPositions = 1,
            MinPositionSizeUsdc = 10m,
            DailyDrawdownPausePct = 0.05m,
        };

        _mockBotConfigRepo.Setup(r => r.GetActiveTrackedAsync()).ReturnsAsync(config);
        _mockValidator.Setup(v => v.Validate(It.IsAny<BotConfiguration>()))
            .Returns(new ConfigValidationResult(true, new List<string>()));
        _mockUow.Setup(u => u.SaveAsync(default)).ReturnsAsync(1);

        var model = new BotConfigViewModel
        {
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0001m,
            CloseThreshold = 0.00005m,
            TotalCapitalUsdc = 107m,
            DefaultLeverage = 5,
            MaxConcurrentPositions = 1,
            MaxCapitalPerPosition = 0.8m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            VolumeFraction = 0.001m,
            BreakevenHoursMax = 6,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            FeeAmortizationHours = 24,
            MinPositionSizeUsdc = 10m,
            MinVolume24hUsdc = 50_000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
        };

        // Act
        var result = await _controller.Index(model);

        // Assert
        var redirectResult = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirectResult.ActionName.Should().Be("Index");
    }

    [Fact]
    public async Task Index_Post_MinHoldExceedsMaxHold_ReturnsViewWithModelError()
    {
        var model = new BotConfigViewModel
        {
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0001m,
            CloseThreshold = 0.00005m,
            TotalCapitalUsdc = 107m,
            DefaultLeverage = 5,
            MaxConcurrentPositions = 1,
            MaxCapitalPerPosition = 0.8m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 24,
            MinHoldTimeHours = 48, // exceeds MaxHoldTimeHours
            VolumeFraction = 0.001m,
            BreakevenHoursMax = 6,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            FeeAmortizationHours = 12,
            MinPositionSizeUsdc = 10m,
            MinVolume24hUsdc = 50_000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
        };

        var result = await _controller.Index(model);

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        _controller.ModelState[nameof(BotConfigViewModel.MinHoldTimeHours)]!
            .Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("must not exceed");
    }

    [Fact]
    public async Task Toggle_WhenArmed_StopsBot()
    {
        var trackedConfig = new BotConfiguration { IsEnabled = true, OperatingState = BotOperatingState.Armed };
        _mockBotConfigRepo.Setup(r => r.GetActiveTrackedAsync()).ReturnsAsync(trackedConfig);
        _mockUow.Setup(u => u.SaveAsync(default)).ReturnsAsync(1);

        var result = await _controller.Toggle();

        trackedConfig.OperatingState.Should().Be(BotOperatingState.Stopped);
        trackedConfig.IsEnabled.Should().BeFalse();
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        // N4: Toggle uses tracked entities directly — no explicit Update() needed
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Toggle_WhenStopped_ArmsBot()
    {
        var trackedConfig = new BotConfiguration { IsEnabled = false, OperatingState = BotOperatingState.Stopped };
        _mockBotConfigRepo.Setup(r => r.GetActiveTrackedAsync()).ReturnsAsync(trackedConfig);
        _mockUow.Setup(u => u.SaveAsync(default)).ReturnsAsync(1);

        var result = await _controller.Toggle();

        trackedConfig.OperatingState.Should().Be(BotOperatingState.Armed);
        trackedConfig.IsEnabled.Should().BeTrue();
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
    }

    [Fact]
    public async Task Index_Post_SaveRoundTrips_AllNewFields()
    {
        // Arrange: entity with defaults
        var config = new BotConfiguration();
        _mockBotConfigRepo.Setup(r => r.GetActiveTrackedAsync()).ReturnsAsync(config);
        _mockValidator.Setup(v => v.Validate(It.IsAny<BotConfiguration>()))
            .Returns(new ConfigValidationResult(true, new List<string>()));
        _mockUow.Setup(u => u.SaveAsync(default)).ReturnsAsync(1);

        var model = new BotConfigViewModel
        {
            // Required fields from existing tests
            OpenThreshold = 0.0003m,
            AlertThreshold = 0.0001m,
            CloseThreshold = -0.00005m,
            TotalCapitalUsdc = 100m,
            DefaultLeverage = 5,
            MaxConcurrentPositions = 1,
            MaxCapitalPerPosition = 0.8m,
            StopLossPct = 0.15m,
            MaxHoldTimeHours = 72,
            MinHoldTimeHours = 2,
            VolumeFraction = 0.001m,
            BreakevenHoursMax = 6,
            AllocationStrategy = AllocationStrategy.Concentrated,
            AllocationTopN = 3,
            FeeAmortizationHours = 24,
            MinPositionSizeUsdc = 10m,
            MinVolume24hUsdc = 50_000m,
            RateStalenessMinutes = 15,
            DailyDrawdownPausePct = 0.05m,
            ConsecutiveLossPause = 3,
            FundingWindowMinutes = 10,
            MaxExposurePerAsset = 0.5m,
            MaxExposurePerExchange = 0.7m,
            TargetPnlMultiplier = 2.0m,
            RebalanceMinImprovement = 0.0002m,
            MaxRebalancesPerCycle = 2,
            MaxLeverageCap = 3,
            MarginUtilizationAlertPct = 0.70m,
            PnlTargetCooldownMinutes = 30,
            // New 19 fields with non-default values
            MinHoldBeforePnlTargetMinutes = 120,
            LiquidationWarningPct = 0.40m,
            LiquidationEarlyWarningPct = 0.80m,
            ExchangeCircuitBreakerThreshold = 5,
            ExchangeCircuitBreakerMinutes = 30,
            PriceFeedFailureCloseThreshold = 20,
            EmergencyCloseSpreadThreshold = -0.005m,
            MinEdgeMultiplier = 4.0m,
            DivergenceAlertMultiplier = 3.0m,
            SlippageBufferBps = 10,
            StablecoinAlertThresholdPct = 0.5m,
            StablecoinCriticalThresholdPct = 2.0m,
            MinConsecutiveFavorableCycles = 5,
            FundingFlipExitCycles = 4,
            UseRiskBasedDivergenceClose = false,
            UseBreakEvenSizeFilter = true,
            DryRunEnabled = true,
            ForceConcurrentExecution = true,
            ReconciliationIntervalCycles = 20,
        };

        // Act
        var result = await _controller.Index(model);

        // Assert: redirects (save succeeded)
        result.Should().BeOfType<RedirectToActionResult>();

        // All 19 new fields should be copied to the entity
        config.MinHoldBeforePnlTargetMinutes.Should().Be(120);
        config.LiquidationWarningPct.Should().Be(0.40m);
        config.LiquidationEarlyWarningPct.Should().Be(0.80m);
        config.ExchangeCircuitBreakerThreshold.Should().Be(5);
        config.ExchangeCircuitBreakerMinutes.Should().Be(30);
        config.PriceFeedFailureCloseThreshold.Should().Be(20);
        config.EmergencyCloseSpreadThreshold.Should().Be(-0.005m);
        config.MinEdgeMultiplier.Should().Be(4.0m);
        config.DivergenceAlertMultiplier.Should().Be(3.0m);
        config.SlippageBufferBps.Should().Be(10);
        config.StablecoinAlertThresholdPct.Should().Be(0.5m);
        config.StablecoinCriticalThresholdPct.Should().Be(2.0m);
        config.MinConsecutiveFavorableCycles.Should().Be(5);
        config.FundingFlipExitCycles.Should().Be(4);
        config.UseRiskBasedDivergenceClose.Should().BeFalse();
        config.UseBreakEvenSizeFilter.Should().BeTrue();
        config.DryRunEnabled.Should().BeTrue();
        config.ForceConcurrentExecution.Should().BeTrue();
        config.ReconciliationIntervalCycles.Should().Be(20);
    }

    [Fact]
    public async Task Index_Get_PopulatesAllNewFieldsFromEntity()
    {
        // Arrange: entity with specific values
        var config = new BotConfiguration
        {
            MinHoldBeforePnlTargetMinutes = 90,
            LiquidationWarningPct = 0.45m,
            LiquidationEarlyWarningPct = 0.85m,
            ExchangeCircuitBreakerThreshold = 7,
            ExchangeCircuitBreakerMinutes = 45,
            PriceFeedFailureCloseThreshold = 15,
            EmergencyCloseSpreadThreshold = -0.002m,
            MinEdgeMultiplier = 5.0m,
            DivergenceAlertMultiplier = 2.5m,
            SlippageBufferBps = 8,
            StablecoinAlertThresholdPct = 0.4m,
            StablecoinCriticalThresholdPct = 1.5m,
            MinConsecutiveFavorableCycles = 4,
            FundingFlipExitCycles = 3,
            UseRiskBasedDivergenceClose = false,
            UseBreakEvenSizeFilter = true,
            DryRunEnabled = true,
            ForceConcurrentExecution = true,
            ReconciliationIntervalCycles = 25,
        };
        _mockBotConfigRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(config);

        // Act
        var result = await _controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<BotConfigViewModel>().Subject;

        model.MinHoldBeforePnlTargetMinutes.Should().Be(90);
        model.LiquidationWarningPct.Should().Be(0.45m);
        model.LiquidationEarlyWarningPct.Should().Be(0.85m);
        model.ExchangeCircuitBreakerThreshold.Should().Be(7);
        model.ExchangeCircuitBreakerMinutes.Should().Be(45);
        model.PriceFeedFailureCloseThreshold.Should().Be(15);
        model.EmergencyCloseSpreadThreshold.Should().Be(-0.002m);
        model.MinEdgeMultiplier.Should().Be(5.0m);
        model.DivergenceAlertMultiplier.Should().Be(2.5m);
        model.SlippageBufferBps.Should().Be(8);
        model.StablecoinAlertThresholdPct.Should().Be(0.4m);
        model.StablecoinCriticalThresholdPct.Should().Be(1.5m);
        model.MinConsecutiveFavorableCycles.Should().Be(4);
        model.FundingFlipExitCycles.Should().Be(3);
        model.UseRiskBasedDivergenceClose.Should().BeFalse();
        model.UseBreakEvenSizeFilter.Should().BeTrue();
        model.DryRunEnabled.Should().BeTrue();
        model.ForceConcurrentExecution.Should().BeTrue();
        model.ReconciliationIntervalCycles.Should().Be(25);
    }
}
