using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Interfaces;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Tests.Unit.Helpers;
using FundingRateArb.Web.Areas.Admin.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class ConnectivityTestControllerTests
{
    private readonly Mock<IConnectivityTestService> _mockConnectivityService = new();
    private readonly Mock<IUserSettingsService> _mockUserSettings = new();
    private readonly Mock<IUnitOfWork> _mockUow = new();
    private readonly Mock<IExchangeRepository> _mockExchangeRepo = new();
    private readonly Mock<UserManager<ApplicationUser>> _mockUserManager;

    public ConnectivityTestControllerTests()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        _mockUserManager = new Mock<UserManager<ApplicationUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);

        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
    }

    private ConnectivityTestController CreateController(ClaimsPrincipal? user = null)
    {
        var controller = new ConnectivityTestController(
            _mockConnectivityService.Object,
            _mockUserSettings.Object,
            _mockUow.Object,
            _mockUserManager.Object);

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
    public async Task RunTest_InvalidUserId_ReturnsBadRequest()
    {
        _mockUserManager
            .Setup(m => m.FindByIdAsync("nonexistent-user"))
            .ReturnsAsync((ApplicationUser?)null);

        var controller = CreateController(CreateAdminUser());

        var result = await controller.RunTest("nonexistent-user", 1);

        result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result;
        badRequest.Value.Should().Be("User not found");
    }

    [Fact]
    public async Task RunTest_ValidAdmin_DelegatesToService()
    {
        var targetUser = new ApplicationUser { Id = "user-1", UserName = "testuser" };
        _mockUserManager
            .Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(targetUser);

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
        var targetUser = new ApplicationUser { Id = "user-1", UserName = "testuser" };
        _mockUserManager
            .Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(targetUser);

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

    [Fact]
    public async Task GetUserExchanges_InvalidUserId_ReturnsBadRequest()
    {
        _mockUserManager
            .Setup(m => m.FindByIdAsync("nonexistent-user"))
            .ReturnsAsync((ApplicationUser?)null);

        var controller = CreateController(CreateAdminUser());

        var result = await controller.GetUserExchanges("nonexistent-user");

        result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result;
        badRequest.Value.Should().Be("User not found");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RunTest_NullOrEmptyUserId_ReturnsBadRequest(string? userId)
    {
        var controller = CreateController(CreateAdminUser());

        var result = await controller.RunTest(userId!, 1);

        result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result;
        badRequest.Value.Should().Be("userId is required");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task RunTest_ZeroOrNegativeExchangeId_ReturnsBadRequest(int exchangeId)
    {
        var controller = CreateController(CreateAdminUser());

        var result = await controller.RunTest("user-1", exchangeId);

        result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result;
        badRequest.Value.Should().Be("Invalid exchangeId");
    }

    [Fact]
    public async Task Index_FiltersOutDataOnlyExchanges()
    {
        var allExchanges = new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid", IsDataOnly = false, IsActive = true },
            new() { Id = 2, Name = "CoinGlass", IsDataOnly = true, IsActive = true },
            new() { Id = 3, Name = "Lighter", IsDataOnly = false, IsActive = true }
        };
        _mockExchangeRepo
            .Setup(r => r.GetActiveAsync())
            .ReturnsAsync(allExchanges);

        var users = new List<ApplicationUser>
        {
            new() { Id = "user-1", UserName = "alice", Email = "alice@test.com" },
            new() { Id = "user-2", UserName = "bob", Email = "bob@test.com" }
        };
        _mockUserManager.Setup(m => m.Users).Returns(users.AsAsyncQueryable());

        var controller = CreateController(CreateAdminUser());

        var result = await controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var exchanges = (List<Exchange>)controller.ViewBag.Exchanges;
        exchanges.Should().HaveCount(2);
        exchanges.Should().OnlyContain(e => !e.IsDataOnly);
        exchanges.Select(e => e.Name).Should().BeEquivalentTo("Hyperliquid", "Lighter");

        // N5: Verify ViewBag.Users is populated
        var viewBagUsers = (IEnumerable<dynamic>)controller.ViewBag.Users;
        ((IEnumerable<dynamic>)viewBagUsers).Should().HaveCount(2);
    }

    [Fact]
    public async Task RunTest_ValidAdmin_ForwardsCancellationToken()
    {
        var targetUser = new ApplicationUser { Id = "user-1", UserName = "testuser" };
        _mockUserManager
            .Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(targetUser);

        var expectedResult = new ConnectivityTestResult(true, "Hyperliquid");
        _mockConnectivityService
            .Setup(s => s.RunTestAsync("admin-123", "user-1", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        using var cts = new CancellationTokenSource();
        var controller = CreateController(CreateAdminUser());
        controller.ControllerContext.HttpContext.RequestAborted = cts.Token;

        var result = await controller.RunTest("user-1", 1);

        _mockConnectivityService.Verify(
            s => s.RunTestAsync("admin-123", "user-1", 1, It.Is<CancellationToken>(t => t == cts.Token)),
            Times.Once);
    }

    [Fact]
    public void RunTest_HasAntiForgeryAttribute()
    {
        var method = typeof(ConnectivityTestController)
            .GetMethod(nameof(ConnectivityTestController.RunTest));

        method.Should().NotBeNull();
        method!.GetCustomAttributes<ValidateAntiForgeryTokenAttribute>()
            .Should().ContainSingle("RunTest must be protected by ValidateAntiForgeryToken");
    }

    [Fact]
    public void ConnectivityTestController_HasAuthorizeAdminAttribute()
    {
        var authorizeAttr = typeof(ConnectivityTestController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .OfType<AuthorizeAttribute>()
            .FirstOrDefault();

        authorizeAttr.Should().NotBeNull("ConnectivityTestController must have [Authorize] attribute");
        authorizeAttr!.Roles.Should().Be("Admin", "ConnectivityTestController must restrict access to Admin role");
    }

    [Fact]
    public async Task GetUserExchanges_ValidUserNoCredentials_ReturnsEmptyArray()
    {
        var targetUser = new ApplicationUser { Id = "user-1", UserName = "testuser" };
        _mockUserManager
            .Setup(m => m.FindByIdAsync("user-1"))
            .ReturnsAsync(targetUser);

        _mockUserSettings
            .Setup(s => s.GetActiveCredentialsAsync("user-1"))
            .ReturnsAsync(new List<UserExchangeCredential>());

        var controller = CreateController(CreateAdminUser());

        var result = await controller.GetUserExchanges("user-1");

        var jsonResult = result.Should().BeOfType<JsonResult>().Subject;
        var exchangeIds = jsonResult.Value.Should().BeAssignableTo<List<int>>().Subject;
        exchangeIds.Should().BeEmpty();
    }
}
