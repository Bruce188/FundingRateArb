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

public class PositionsControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IExecutionEngine> _mockExecution = new();

    public PositionsControllerTests()
    {
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
    }

    private PositionsController CreateControllerForUser(ClaimsPrincipal user)
    {
        var controller = new PositionsController(_mockUow.Object, _mockExecution.Object,
            NullLogger<PositionsController>.Instance);

        var httpContext = new DefaultHttpContext { User = user };
        var tempDataProvider = Mock.Of<ITempDataProvider>();
        var tempData = new TempDataDictionary(httpContext, tempDataProvider);

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = tempData;
        return controller;
    }

    private static ClaimsPrincipal TraderUser(string userId = "trader-id") =>
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, "Trader"),
        }, "mock"));

    private static ClaimsPrincipal AdminUser(string userId = "admin-id") =>
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "mock"));

    private static ArbitragePosition OpenPositionOwnedBy(string userId) =>
        new ArbitragePosition
        {
            Id = 1,
            UserId = userId,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
        };

    // Test 1: Trader trying to close a position owned by someone else gets Forbid
    [Fact]
    public async Task Close_WhenNotOwner_ReturnsForbid()
    {
        // Arrange
        var position = OpenPositionOwnedBy("other-user-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert
        result.Should().BeOfType<ForbidResult>();
        _mockExecution.Verify(e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 2: Admin can close any position regardless of ownership
    [Fact]
    public async Task Close_WhenAdmin_CanCloseAnyPosition()
    {
        // Arrange
        var position = OpenPositionOwnedBy("some-other-user-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync(It.IsAny<string>(), position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        _mockExecution.Verify(e => e.ClosePositionAsync(It.IsAny<string>(), position, CloseReason.Manual, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Test 3: Closing an already-closed position returns BadRequest
    [Fact]
    public async Task Close_WhenAlreadyClosed_ReturnsBadRequest()
    {
        // Arrange
        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = "trader-id",
            Status = PositionStatus.Closed,
            OpenedAt = DateTime.UtcNow.AddHours(-5),
        };
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        _mockExecution.Verify(e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 4: Happy path - owner closes own open position, gets redirect
    [Fact]
    public async Task Close_Success_RedirectsToIndex()
    {
        // Arrange
        var position = OpenPositionOwnedBy("trader-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync(It.IsAny<string>(), position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Success"].Should().Be("Position closed successfully.");
        _mockExecution.Verify(e => e.ClosePositionAsync(It.IsAny<string>(), position, CloseReason.Manual, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Test 5: Trader's Index only returns positions owned by that trader
    [Fact]
    public async Task Index_TraderSeesOnlyOwnPositions()
    {
        // Arrange
        var traderId = "trader-id";
        var ownPositions = new List<ArbitragePosition>
        {
            new ArbitragePosition { Id = 1, UserId = traderId, Status = PositionStatus.Open, OpenedAt = DateTime.UtcNow },
            new ArbitragePosition { Id = 2, UserId = traderId, Status = PositionStatus.Closed, OpenedAt = DateTime.UtcNow.AddDays(-1) },
        };
        _mockPositions.Setup(p => p.GetByUserAsync(traderId, It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(ownPositions);

        var controller = CreateControllerForUser(TraderUser(traderId));

        // Act
        var result = await controller.Index();

        // Assert
        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<PositionIndexViewModel>().Subject;
        vm.Positions.Should().HaveCount(2);
        _mockPositions.Verify(p => p.GetByUserAsync(traderId, It.IsAny<int>(), It.IsAny<int>()), Times.Once);
        _mockPositions.Verify(p => p.GetAllAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }
}
