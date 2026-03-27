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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class PositionsControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IPositionRepository> _mockPositions = new();
    private readonly Mock<IAlertRepository> _mockAlerts = new();
    private readonly Mock<IExecutionEngine> _mockExecution = new();

    public PositionsControllerTests()
    {
        _mockUow.Setup(u => u.Positions).Returns(_mockPositions.Object);
        _mockUow.Setup(u => u.Alerts).Returns(_mockAlerts.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
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

    // Test 2: Admin can close any position regardless of ownership — uses position owner's userId
    [Fact]
    public async Task Close_WhenAdmin_CanCloseAnyPosition()
    {
        // Arrange
        var position = OpenPositionOwnedBy("some-other-user-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("some-other-user-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Callback<string, ArbitragePosition, CloseReason, CancellationToken>((_, p, _, _) => p.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        // Verify the position owner's userId is passed (not the admin's userId)
        _mockExecution.Verify(e => e.ClosePositionAsync("some-other-user-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()), Times.Once);
        // Verify durable audit alert is persisted with ActingUserId
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al =>
            al.UserId == "some-other-user-id" &&
            al.ActingUserId == "admin-id" &&
            al.Severity == AlertSeverity.Info &&
            al.Type == AlertType.PositionClosed)), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
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

    // Test 4: Happy path - owner closes own open position, gets redirect (no audit alert)
    [Fact]
    public async Task Close_Success_RedirectsToIndex()
    {
        // Arrange
        var position = OpenPositionOwnedBy("trader-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("trader-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Callback<string, ArbitragePosition, CloseReason, CancellationToken>((_, p, _, _) => p.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Success"].Should().Be("Position closed successfully.");
        _mockExecution.Verify(e => e.ClosePositionAsync("trader-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()), Times.Once);
        // No admin audit alert when owner closes their own position
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.ActingUserId != null)), Times.Never);
    }

    // Test 5: Admin closing position with null UserId — engine handles gracefully
    [Fact]
    public async Task Close_WhenPositionHasNullUserId_EngineHandlesGracefully()
    {
        // Arrange: position from legacy data where UserId is null
        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = null!,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
        };
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync(null!, position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Callback<string, ArbitragePosition, CloseReason, CancellationToken>((_, p, _, _) => p.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert: controller still redirects (engine handles null userId internally)
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        // Verify the position owner's (null) userId is passed, not the admin's
        _mockExecution.Verify(e => e.ClosePositionAsync(null!, position, CloseReason.Manual, It.IsAny<CancellationToken>()), Times.Once);
    }

    // Test 6: Non-admin closing position with null UserId returns Forbid
    [Fact]
    public async Task Close_WhenNonAdminAndPositionHasNullUserId_ReturnsForbid()
    {
        // Position from legacy data where UserId is null — ownership check fails for non-admin
        var position = new ArbitragePosition
        {
            Id = 1,
            UserId = null!,
            Status = PositionStatus.Open,
            OpenedAt = DateTime.UtcNow.AddHours(-2),
        };
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        var result = await controller.Close(position.Id);

        result.Should().BeOfType<ForbidResult>();
        _mockExecution.Verify(e => e.ClosePositionAsync(It.IsAny<string>(), It.IsAny<ArbitragePosition>(), It.IsAny<CloseReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 7: ClosePositionAsync throws — no audit alert, no success TempData
    [Fact]
    public async Task Close_WhenEngineThrows_ReturnsErrorFeedback()
    {
        // Arrange
        var position = OpenPositionOwnedBy("some-other-user-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("some-other-user-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Exchange timeout"));

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert — redirects with exact error message, no audit alert, no success message
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Error"].Should().Be("Failed to close position. Please try again or contact support.");
        controller.TempData.ContainsKey("Success").Should().BeFalse();
        _mockAlerts.Verify(a => a.Add(It.IsAny<Alert>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 8: ClosePositionAsync returns normally but position not closed — no audit, error feedback
    [Fact]
    public async Task Close_WhenEngineSilentlyFails_ReturnsErrorFeedback()
    {
        // Arrange — engine returns without error but position stays Open
        var position = OpenPositionOwnedBy("some-other-user-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("some-other-user-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // Does NOT change position.Status

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert — redirects with error, no audit alert, no success message
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Error"].Should().NotBeNull();
        controller.TempData.ContainsKey("Success").Should().BeFalse();
        _mockAlerts.Verify(a => a.Add(It.IsAny<Alert>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 8b: Non-admin trader gets error feedback on silent close failure, no audit alert
    [Fact]
    public async Task Close_WhenEngineSilentlyFails_TraderGetsErrorFeedback()
    {
        // Arrange — engine returns without error but position stays Open (non-admin user)
        var position = OpenPositionOwnedBy("trader-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("trader-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // Does NOT change position.Status

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert — redirects with error, no audit alert persisted, no success message
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Error"].Should().Be("Position close was submitted but did not complete. Check position status.");
        controller.TempData.ContainsKey("Success").Should().BeFalse();
        _mockAlerts.Verify(a => a.Add(It.IsAny<Alert>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 9: When engine throws, position status remains Open
    [Fact]
    public async Task Close_WhenEngineThrows_PositionStatusRemainsOpen()
    {
        // Arrange
        var position = OpenPositionOwnedBy("trader-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("trader-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Exchange timeout"));

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        await controller.Close(position.Id);

        // Assert — position status must remain Open after failed close
        position.Status.Should().Be(PositionStatus.Open);
    }

    // Test 10: OperationCanceledException propagates instead of being swallowed
    [Fact]
    public async Task Close_WhenCancelled_PropagatesCancellation()
    {
        // Arrange
        var position = OpenPositionOwnedBy("trader-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("trader-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act & Assert — OperationCanceledException should propagate, not be caught
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.Close(position.Id));
    }

    // Test 11: Admin close succeeds even when audit save fails
    [Fact]
    public async Task Close_WhenAuditSaveFails_StillReturnsSuccess()
    {
        // Arrange
        var position = OpenPositionOwnedBy("some-other-user-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("some-other-user-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Callback<string, ArbitragePosition, CloseReason, CancellationToken>((_, p, _, _) => p.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("DB connection lost", new Exception()));

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert — close succeeded, so user sees success despite audit failure
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Success"].Should().Be("Position closed successfully.");

        // Verify the audit alert was attempted before the save threw
        _mockAlerts.Verify(a => a.Add(It.Is<Alert>(al => al.Type == AlertType.PositionClosed)), Times.Once);
    }

    // Test 12: Admin closing their own position does not create an audit alert
    [Fact]
    public async Task Close_AdminClosingOwnPosition_NoAuditAlertCreated()
    {
        // Arrange — admin's userId matches position.UserId
        var position = OpenPositionOwnedBy("admin-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("admin-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Callback<string, ArbitragePosition, CloseReason, CancellationToken>((_, p, _, _) => p.Status = PositionStatus.Closed)
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(AdminUser("admin-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert — close succeeds but no audit alert since admin is closing own position
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Success"].Should().Be("Position closed successfully.");
        _mockAlerts.Verify(a => a.Add(It.IsAny<Alert>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // Test 13: Trader's Index only returns positions owned by that trader
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

    // ── Post-close status verification for non-Closed statuses ──

    [Theory]
    [InlineData(PositionStatus.Closing)]
    [InlineData(PositionStatus.EmergencyClosed)]
    public async Task Close_WhenEngineSetNonClosedStatus_ReturnsDidNotCompleteError(PositionStatus postCloseStatus)
    {
        // Arrange — engine sets status to a non-Closed value via callback
        var position = OpenPositionOwnedBy("trader-id");
        _mockPositions.Setup(p => p.GetByIdAsync(position.Id))
            .ReturnsAsync(position);
        _mockExecution.Setup(e => e.ClosePositionAsync("trader-id", position, CloseReason.Manual, It.IsAny<CancellationToken>()))
            .Callback<string, ArbitragePosition, CloseReason, CancellationToken>((_, p, _, _) => p.Status = postCloseStatus)
            .Returns(Task.CompletedTask);

        var controller = CreateControllerForUser(TraderUser("trader-id"));

        // Act
        var result = await controller.Close(position.Id);

        // Assert — controller treats anything except Closed as incomplete
        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be(nameof(PositionsController.Index));
        controller.TempData["Error"].Should().Be("Position close was submitted but did not complete. Check position status.");
        controller.TempData.ContainsKey("Success").Should().BeFalse();
        _mockAlerts.Verify(a => a.Add(It.IsAny<Alert>()), Times.Never);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
