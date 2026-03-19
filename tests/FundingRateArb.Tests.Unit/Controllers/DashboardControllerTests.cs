using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Interfaces;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Controllers;
using FundingRateArb.Web.ViewModels;

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

        _mockUow.Setup(u => u.BotConfig).Returns(_mockBotConfigRepo.Object);
        _mockUow.Setup(u => u.Positions).Returns(_mockPositionRepo.Object);
        _mockUow.Setup(u => u.FundingRates).Returns(_mockFundingRateRepo.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlertRepo.Object);

        _mockBotConfigRepo.Setup(r => r.GetActiveAsync())
            .ReturnsAsync(new BotConfiguration { IsEnabled = false });
        _mockPositionRepo.Setup(r => r.GetOpenAsync())
            .ReturnsAsync([]);
        _mockFundingRateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync([]);
        _mockAlertRepo.Setup(r => r.GetByUserAsync(It.IsAny<string>(), true, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync([]);
        _mockSignalEngine.Setup(s => s.GetOpportunitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _controller = new DashboardController(_mockUow.Object, _mockLogger.Object, _mockSignalEngine.Object, _mockBotControl.Object);

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
        _mockPositionRepo.Setup(r => r.GetOpenAsync()).ReturnsAsync(positions);

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
}
