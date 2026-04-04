using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Web.Areas.Admin.Controllers;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class ExchangeControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IExchangeRepository> _mockExchangeRepo;
    private readonly ExchangeController _controller;

    public ExchangeControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockExchangeRepo = new Mock<IExchangeRepository>();

        _mockUow.Setup(u => u.Exchanges).Returns(_mockExchangeRepo.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _controller = new ExchangeController(
            _mockUow.Object, Mock.Of<ILogger<ExchangeController>>());

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
    public async Task Index_ReturnsViewWithExchanges()
    {
        var exchanges = new List<Exchange>
        {
            new() { Id = 1, Name = "Hyperliquid" },
            new() { Id = 2, Name = "Lighter" },
        };
        _mockExchangeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(exchanges);

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        viewResult.Model.Should().BeEquivalentTo(exchanges);
    }

    [Fact]
    public async Task Create_Post_ValidModel_RedirectsToIndex()
    {
        var model = new ExchangeCreateViewModel
        {
            Name = "TestExchange",
            ApiBaseUrl = "https://api.test.com",
            WsBaseUrl = "wss://ws.test.com",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
        };

        var result = await _controller.Create(model);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _mockExchangeRepo.Verify(r => r.Add(It.IsAny<Exchange>()), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_Post_InvalidModel_ReturnsView()
    {
        _controller.ModelState.AddModelError("Name", "Required");
        var model = new ExchangeCreateViewModel
        {
            ApiBaseUrl = "https://api.test.com",
            WsBaseUrl = "wss://ws.test.com",
        };

        var result = await _controller.Create(model);

        result.Should().BeOfType<ViewResult>();
        _mockExchangeRepo.Verify(r => r.Add(It.IsAny<Exchange>()), Times.Never);
    }

    [Fact]
    public async Task Create_Post_InvalidApiUrl_ReturnsView()
    {
        var model = new ExchangeCreateViewModel
        {
            Name = "TestExchange",
            ApiBaseUrl = "http://not-https.com",
            WsBaseUrl = "wss://ws.test.com",
            FundingInterval = FundingInterval.Hourly,
        };

        var result = await _controller.Create(model);

        result.Should().BeOfType<ViewResult>();
        _controller.ModelState.IsValid.Should().BeFalse();
        _mockExchangeRepo.Verify(r => r.Add(It.IsAny<Exchange>()), Times.Never);
    }

    [Fact]
    public async Task Edit_Get_ExistingId_ReturnsViewWithExchange()
    {
        var exchange = new Exchange
        {
            Id = 1,
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hl.com",
            WsBaseUrl = "wss://ws.hl.com",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true,
        };
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(exchange);

        var result = await _controller.Edit(1);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<ExchangeEditViewModel>().Subject;
        model.Name.Should().Be("Hyperliquid");
        model.Id.Should().Be(1);
    }

    [Fact]
    public async Task Edit_Get_NotFound_ReturnsNotFound()
    {
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(99)).ReturnsAsync((Exchange?)null);

        var result = await _controller.Edit(99);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Edit_Post_ValidModel_RedirectsToIndex()
    {
        var exchange = new Exchange
        {
            Id = 1,
            Name = "Hyperliquid",
            ApiBaseUrl = "https://api.hl.com",
            WsBaseUrl = "wss://ws.hl.com",
            FundingInterval = FundingInterval.Hourly,
            IsActive = true,
        };
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(exchange);

        var model = new ExchangeEditViewModel
        {
            Id = 1,
            Name = "Hyperliquid Updated",
            ApiBaseUrl = "https://api.hl-v2.com",
            WsBaseUrl = "wss://ws.hl-v2.com",
            FundingInterval = FundingInterval.Hourly,
            FundingIntervalHours = 1,
            IsActive = true,
        };

        var result = await _controller.Edit(1, model);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _mockExchangeRepo.Verify(r => r.Update(It.IsAny<Exchange>()), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_Post_Success_RedirectsToIndex()
    {
        var exchange = new Exchange { Id = 1, Name = "Hyperliquid" };
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(exchange);

        var result = await _controller.DeleteConfirmed(1);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _mockExchangeRepo.Verify(r => r.Remove(exchange), Times.Once);
    }

    [Fact]
    public async Task Delete_Post_ForeignKeyViolation_ReturnsErrorRedirect()
    {
        var exchange = new Exchange { Id = 1, Name = "Hyperliquid" };
        _mockExchangeRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(exchange);
        _mockExchangeRepo.Setup(r => r.Remove(exchange));
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("FK violation", new Exception()));

        var result = await _controller.DeleteConfirmed(1);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Delete");
        _controller.TempData["Error"].Should().NotBeNull();
    }
}
