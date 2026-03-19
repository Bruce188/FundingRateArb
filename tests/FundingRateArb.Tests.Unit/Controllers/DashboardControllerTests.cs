using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using FundingRateArb.Application.Common.Repositories;
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
    private readonly DashboardController _controller;

    public DashboardControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockBotConfigRepo = new Mock<IBotConfigRepository>();
        _mockPositionRepo = new Mock<IPositionRepository>();
        _mockFundingRateRepo = new Mock<IFundingRateRepository>();
        _mockAlertRepo = new Mock<IAlertRepository>();
        _mockLogger = new Mock<ILogger<DashboardController>>();

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
        _mockAlertRepo.Setup(r => r.GetByUserAsync(It.IsAny<string>(), true))
            .ReturnsAsync([]);

        _controller = new DashboardController(_mockUow.Object, _mockLogger.Object);

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
}
