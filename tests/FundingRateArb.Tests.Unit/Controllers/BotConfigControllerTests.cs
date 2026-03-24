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
}
