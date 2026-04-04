using System.Security.Claims;
using FluentAssertions;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.Areas.Admin.Controllers;
using FundingRateArb.Web.ViewModels.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class AssetControllerTests
{
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly Mock<IAssetRepository> _mockAssetRepo;
    private readonly AssetController _controller;

    public AssetControllerTests()
    {
        _mockUow = new Mock<IUnitOfWork>();
        _mockAssetRepo = new Mock<IAssetRepository>();

        _mockUow.Setup(u => u.Assets).Returns(_mockAssetRepo.Object);
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        _controller = new AssetController(
            _mockUow.Object, Mock.Of<ILogger<AssetController>>());

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
    public async Task Index_ReturnsViewWithAssets()
    {
        var assets = new List<Asset>
        {
            new() { Id = 1, Symbol = "ETH", Name = "Ethereum" },
            new() { Id = 2, Symbol = "BTC", Name = "Bitcoin" },
        };
        _mockAssetRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(assets);

        var result = await _controller.Index();

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        viewResult.Model.Should().BeEquivalentTo(assets);
    }

    [Fact]
    public async Task Create_Post_ValidModel_RedirectsToIndex()
    {
        var model = new AssetCreateViewModel
        {
            Symbol = "ETH",
            Name = "Ethereum",
            IsActive = true,
        };

        var result = await _controller.Create(model);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _mockAssetRepo.Verify(r => r.Add(It.IsAny<Asset>()), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_Post_InvalidModel_ReturnsView()
    {
        _controller.ModelState.AddModelError("Symbol", "Required");
        var model = new AssetCreateViewModel { Name = "Ethereum" };

        var result = await _controller.Create(model);

        result.Should().BeOfType<ViewResult>();
        _mockAssetRepo.Verify(r => r.Add(It.IsAny<Asset>()), Times.Never);
    }

    [Fact]
    public async Task Create_Post_NormalizesSymbolToUpperCase()
    {
        Asset? savedAsset = null;
        _mockAssetRepo.Setup(r => r.Add(It.IsAny<Asset>()))
            .Callback<Asset>(a => savedAsset = a);

        var model = new AssetCreateViewModel
        {
            Symbol = "eth",
            Name = "Ethereum",
            IsActive = true,
        };

        await _controller.Create(model);

        savedAsset.Should().NotBeNull();
        savedAsset!.Symbol.Should().Be("ETH");
    }

    [Fact]
    public async Task Edit_Get_ExistingId_ReturnsView()
    {
        var asset = new Asset { Id = 1, Symbol = "ETH", Name = "Ethereum", IsActive = true };
        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(asset);

        var result = await _controller.Edit(1);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var model = viewResult.Model.Should().BeOfType<AssetEditViewModel>().Subject;
        model.Symbol.Should().Be("ETH");
        model.Id.Should().Be(1);
    }

    [Fact]
    public async Task Edit_Get_NotFound_ReturnsNotFound()
    {
        _mockAssetRepo.Setup(r => r.GetByIdAsync(99)).ReturnsAsync((Asset?)null);

        var result = await _controller.Edit(99);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Edit_Post_ValidModel_Redirects()
    {
        var asset = new Asset { Id = 1, Symbol = "ETH", Name = "Ethereum", IsActive = true };
        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(asset);

        var model = new AssetEditViewModel
        {
            Id = 1,
            Symbol = "ETH",
            Name = "Ethereum Updated",
            IsActive = true,
        };

        var result = await _controller.Edit(1, model);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _mockAssetRepo.Verify(r => r.Update(It.IsAny<Asset>()), Times.Once);
        _mockUow.Verify(u => u.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Edit_Post_NormalizesSymbolToUpperCase()
    {
        var asset = new Asset { Id = 1, Symbol = "ETH", Name = "Ethereum", IsActive = true };
        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(asset);

        var model = new AssetEditViewModel
        {
            Id = 1,
            Symbol = "btc",
            Name = "Bitcoin",
            IsActive = true,
        };

        await _controller.Edit(1, model);

        asset.Symbol.Should().Be("BTC");
    }

    [Fact]
    public async Task Delete_Post_Success_Redirects()
    {
        var asset = new Asset { Id = 1, Symbol = "ETH", Name = "Ethereum" };
        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(asset);

        var result = await _controller.DeleteConfirmed(1);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _mockAssetRepo.Verify(r => r.Remove(asset), Times.Once);
    }

    [Fact]
    public async Task Delete_Post_ForeignKeyViolation_ReturnsError()
    {
        var asset = new Asset { Id = 1, Symbol = "ETH", Name = "Ethereum" };
        _mockAssetRepo.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(asset);
        _mockAssetRepo.Setup(r => r.Remove(asset));
        _mockUow.Setup(u => u.SaveAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("FK violation", new Exception()));

        var result = await _controller.DeleteConfirmed(1);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Delete");
        _controller.TempData["Error"].Should().NotBeNull();
    }
}
