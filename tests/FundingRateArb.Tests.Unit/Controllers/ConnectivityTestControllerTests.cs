using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.Areas.Admin.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class ConnectivityTestControllerTests
{
    private readonly Mock<IConnectivityTestService> _mockConnectivityService = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();

    private ConnectivityTestController CreateController(ClaimsPrincipal? user = null)
    {
        var store = new Mock<IUserStore<IdentityUser>>();
        var userManager = new Mock<UserManager<IdentityUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        var controller = new ConnectivityTestController(
            _mockConnectivityService.Object,
            _mockUserSettings.Object,
            _mockUow.Object,
            userManager.Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        return controller;
    }

    private static ClaimsPrincipal CreateAdminUser(string userId = "admin-123")
    {
        return new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Role, "Admin"),
        }, "mock"));
    }

    [Fact]
    public async Task RunTest_NoAdminClaim_ReturnsUnauthorized()
    {
        var controller = CreateController(new ClaimsPrincipal(new ClaimsIdentity()));

        var result = await controller.RunTest("user-1", 1);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task RunTest_ValidAdmin_DelegatesToService()
    {
        var expectedResult = new ConnectivityTestResult(true, "Hyperliquid");
        _mockConnectivityService
            .Setup(s => s.RunTestAsync("admin-123", "user-1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var controller = CreateController(CreateAdminUser());

        var result = await controller.RunTest("user-1", 1);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        jsonResult.Value.Should().Be(expectedResult);

        _mockConnectivityService.Verify(
            s => s.RunTestAsync("admin-123", "user-1", 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetUserExchanges_EmptyUserId_ReturnsEmptyArray()
    {
        var controller = CreateController(CreateAdminUser());

        var result = await controller.GetUserExchanges("");

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var exchangeIds = jsonResult.Value.Should().BeAssignableTo<int[]>().Subject;
        exchangeIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserExchanges_NullUserId_ReturnsEmptyArray()
    {
        var controller = CreateController(CreateAdminUser());

        var result = await controller.GetUserExchanges(null!);

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var exchangeIds = jsonResult.Value.Should().BeAssignableTo<int[]>().Subject;
        exchangeIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserExchanges_ValidUserId_ReturnsExchangeIds()
    {
        var credentials = new List<UserExchangeCredential>
        {
            new() { ExchangeId = 1, UserId = "user-1", IsActive = true },
            new() { ExchangeId = 3, UserId = "user-1", IsActive = true }
        };
        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user-1"))
            .ReturnsAsync(credentials);

        var controller = CreateController(CreateAdminUser());

        var result = await controller.GetUserExchanges("user-1");

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var exchangeIds = jsonResult.Value.Should().BeAssignableTo<List<int>>().Subject;
        exchangeIds.Should().BeEquivalentTo(new[] { 1, 3 });
    }
}
