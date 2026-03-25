using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Controllers;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class SettingsControllerTests
{
    private readonly Mock<IUserSettingsService> _mockSettings = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IBotConfigRepository> _mockBotConfigRepo = new();
    private readonly SettingsController _controller;

    private static readonly BotConfiguration GlobalConfig = new()
    {
        IsEnabled = true,
        TotalCapitalUsdc = 5000m,
        DefaultLeverage = 5,
        MaxConcurrentPositions = 3,
        MaxCapitalPerPosition = 0.5m,
        OpenThreshold = 0.0003m,
        CloseThreshold = 0.0001m,
        AlertThreshold = 0.00015m,
        StopLossPct = 0.15m,
        MaxHoldTimeHours = 48,
        DailyDrawdownPausePct = 0.05m,
        ConsecutiveLossPause = 3,
        MaxExposurePerAsset = 0.5m,
        MaxExposurePerExchange = 0.7m,
        AllocationStrategy = AllocationStrategy.Concentrated,
        AllocationTopN = 5,
        FeeAmortizationHours = 24,
        MinPositionSizeUsdc = 10m,
        MinVolume24hUsdc = 100000m,
        RateStalenessMinutes = 10,
        FundingWindowMinutes = 30,
        UpdatedByUserId = "admin-user-id",
    };

    public SettingsControllerTests()
    {
        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfigRepo.Object);
        _mockBotConfigRepo.Setup(b => b.GetActiveAsync()).ReturnsAsync(GlobalConfig);

        _controller = new SettingsController(
            _mockSettings.Object,
            _mockUow.Object,
            NullLogger<SettingsController>.Instance);
    }

    private void SetupAuthenticatedUser()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user-id"),
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
    public async Task Configuration_Get_ReturnsViewWithAdminDefaults()
    {
        SetupAuthenticatedUser();
        _mockSettings
            .Setup(s => s.GetOrCreateConfigAsync("test-user-id"))
            .ReturnsAsync(new UserConfiguration
            {
                IsEnabled = true,
                TotalCapitalUsdc = 1000m,
                DefaultLeverage = 3,
                MaxConcurrentPositions = 2,
                MaxCapitalPerPosition = 0.5m,
                OpenThreshold = 0.0003m,
                CloseThreshold = 0.0001m,
                AlertThreshold = 0.00015m,
                StopLossPct = 0.15m,
                MaxHoldTimeHours = 48,
                AllocationStrategy = AllocationStrategy.Concentrated,
                AllocationTopN = 5,
                FeeAmortizationHours = 24m,
                MinPositionSizeUsdc = 10m,
                MinVolume24hUsdc = 100000m,
                RateStalenessMinutes = 10,
                DailyDrawdownPausePct = 0.05m,
                ConsecutiveLossPause = 3,
                FundingWindowMinutes = 30,
                MaxExposurePerAsset = 0.5m,
                MaxExposurePerExchange = 0.7m,
            });

        var result = await _controller.Configuration();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<UserConfigViewModel>().Subject;

        // AdminDefaults must be populated from the global BotConfiguration
        model.AdminDefaults.Should().NotBeNull();
        model.AdminDefaults!.TotalCapitalUsdc.Should().Be(GlobalConfig.TotalCapitalUsdc);
        model.AdminDefaults.DefaultLeverage.Should().Be(GlobalConfig.DefaultLeverage);
        model.AdminDefaults.MaxConcurrentPositions.Should().Be(GlobalConfig.MaxConcurrentPositions);
        model.AdminDefaults.OpenThreshold.Should().Be(GlobalConfig.OpenThreshold);
        model.AdminDefaults.StopLossPct.Should().Be(GlobalConfig.StopLossPct);
    }

    [Fact]
    public async Task Configuration_Get_Unauthenticated_ReturnsUnauthorized()
    {
        // No user claims set
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };

        var result = await _controller.Configuration();

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Configuration_Get_NullGlobalConfig_ReturnsViewWithEmptyDefaults()
    {
        SetupAuthenticatedUser();
        _mockBotConfigRepo.Setup(b => b.GetActiveAsync()).Returns(Task.FromResult<BotConfiguration>(null!));
        _mockSettings
            .Setup(s => s.GetOrCreateConfigAsync("test-user-id"))
            .ReturnsAsync(new UserConfiguration
            {
                IsEnabled = true,
                TotalCapitalUsdc = 1000m,
                DefaultLeverage = 3,
                MaxConcurrentPositions = 2,
            });

        // Should not throw NullReferenceException
        var result = await _controller.Configuration();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<UserConfigViewModel>().Subject;

        // AdminDefaults should be an empty DTO with default values, not null
        model.AdminDefaults.Should().NotBeNull();
        model.AdminDefaults!.TotalCapitalUsdc.Should().Be(0m);
        model.AdminDefaults.DefaultLeverage.Should().Be(0);
    }

    [Fact]
    public async Task ResetConfiguration_NullGlobalConfig_RedirectsWithErrorMessage()
    {
        SetupAuthenticatedUser();
        _mockBotConfigRepo.Setup(b => b.GetActiveAsync()).Returns(Task.FromResult<BotConfiguration>(null!));

        var result = await _controller.ResetConfiguration();

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Configuration");
        _controller.TempData["Error"].Should().Be("No global configuration found. Cannot reset to defaults.");
    }
}
