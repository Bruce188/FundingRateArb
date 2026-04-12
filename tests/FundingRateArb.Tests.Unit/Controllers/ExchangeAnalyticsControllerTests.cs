using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Web.Areas.Admin.Controllers;
using FundingRateArb.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Controllers;

public class ExchangeAnalyticsControllerTests
{
    private readonly Mock<IExchangeAnalyticsService> _mockAnalytics = new();
    private readonly Mock<ICoinGlassAnalyticsRepository> _mockRepo = new();
    private readonly Mock<ICoinGlassScreeningProvider> _mockScreening = new();
    private readonly ExchangeAnalyticsController _controller;

    public ExchangeAnalyticsControllerTests()
    {
        _mockRepo.Setup(r => r.GetLatestSnapshotPerExchangeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoinGlassExchangeRate>());
        _mockAnalytics.Setup(a => a.GetExchangeOverviewAsync(It.IsAny<List<CoinGlassExchangeRate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ExchangeOverviewDto>());
        _mockAnalytics.Setup(a => a.GetTopOpportunitiesAsync(It.IsAny<List<CoinGlassExchangeRate>>(), It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SpreadOpportunityDto>());
        _mockAnalytics.Setup(a => a.GetRateComparisonsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RateComparisonDto>());
        _mockAnalytics.Setup(a => a.GetRecentDiscoveryEventsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveryEventDto>());

        _controller = new ExchangeAnalyticsController(
            _mockAnalytics.Object,
            _mockRepo.Object,
            _mockScreening.Object,
            NullLogger<ExchangeAnalyticsController>.Instance);

        var httpContext = new DefaultHttpContext();
        var tempDataProvider = Mock.Of<ITempDataProvider>();
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.TempData = new TempDataDictionary(httpContext, tempDataProvider);
    }

    [Fact]
    public async Task Index_ReturnsView_WithEmptyData()
    {
        var result = await _controller.Index(CancellationToken.None);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<ExchangeAnalyticsViewModel>().Subject;
        vm.CoinGlassAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Index_ReturnsView_WithCoinGlassAvailable()
    {
        _mockScreening.Setup(s => s.IsAvailable).Returns(true);

        var result = await _controller.Index(CancellationToken.None);

        var viewResult = result.Should().BeOfType<ViewResult>().Subject;
        var vm = viewResult.Model.Should().BeOfType<ExchangeAnalyticsViewModel>().Subject;
        vm.CoinGlassAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task TriggerFetch_Success_SetsTempDataAndRedirects()
    {
        var hotSymbols = new HashSet<string> { "ETH", "BTC" } as IReadOnlySet<string>;
        _mockScreening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(hotSymbols);

        var result = await _controller.TriggerFetch(CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _controller.TempData["Success"].Should().NotBeNull();
        _controller.TempData["Success"]!.ToString().Should().Contain("2 hot symbols");
    }

    [Fact]
    public async Task TriggerFetch_ProviderThrows_SetsTempDataErrorAndRedirects()
    {
        _mockScreening.Setup(s => s.GetHotSymbolsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused to api.coinglass.com"));

        var result = await _controller.TriggerFetch(CancellationToken.None);

        var redirect = result.Should().BeOfType<RedirectToActionResult>().Subject;
        redirect.ActionName.Should().Be("Index");
        _controller.TempData["Error"].Should().NotBeNull();
        var errorMsg = _controller.TempData["Error"]!.ToString()!;
        errorMsg.Should().Contain("CoinGlass fetch failed");
        errorMsg.Should().NotContain("Connection refused", "exception message should not leak to user");
        errorMsg.Should().Contain("Check logs for details");
    }
}
