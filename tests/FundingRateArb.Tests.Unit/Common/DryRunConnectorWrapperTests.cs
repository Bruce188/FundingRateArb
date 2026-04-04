using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.Common;

public class DryRunConnectorWrapperTests
{
    private readonly Mock<IExchangeConnector> _mockInner = new();
    private readonly ILogger _logger = NullLogger.Instance;

    public DryRunConnectorWrapperTests()
    {
        _mockInner.Setup(c => c.ExchangeName).Returns("TestExchange");
        _mockInner.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3000m);
    }

    [Fact]
    public async Task PlaceMarketOrderAsync_LongSide_ReturnsSimulatedFillWithPositiveSlippage()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 300m, 5);

        result.Success.Should().BeTrue();
        result.FilledQuantity.Should().Be(300m / 3000m);
        result.FilledPrice.Should().Be(3000m * 1.001m);
        result.OrderId.Should().StartWith("DRY-");
    }

    [Fact]
    public async Task PlaceMarketOrderAsync_ShortSide_ReturnsSimulatedFillWithNegativeSlippage()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Short, 300m, 5);

        result.Success.Should().BeTrue();
        result.FilledPrice.Should().Be(3000m * 0.999m);
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_ReturnsProvidedQuantityWithSlippage()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, 0.5m, 5);

        result.Success.Should().BeTrue();
        result.FilledQuantity.Should().Be(0.5m);
        result.FilledPrice.Should().Be(3000m * 1.001m);
    }

    [Fact]
    public async Task GetMarkPriceAsync_DelegatesToInner()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var price = await sut.GetMarkPriceAsync("ETH");

        price.Should().Be(3000m);
        _mockInner.Verify(c => c.GetMarkPriceAsync("ETH", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClosePositionAsync_ReturnsSimulatedResult()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        result.FilledPrice.Should().Be(3000m * 0.999m);
        result.OrderId.Should().StartWith("DRY-");
    }

    [Fact]
    public async Task VerifyPositionOpenedAsync_ReturnsTrue()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.VerifyPositionOpenedAsync("ETH", Side.Long);

        result.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DisposesInnerWhenDisposable()
    {
        var mockDisposable = new Mock<IExchangeConnector>();
        var disposableConnector = mockDisposable.As<IDisposable>();

        var sut = new DryRunConnectorWrapper(mockDisposable.Object, _logger);
        sut.Dispose();

        disposableConnector.Verify(d => d.Dispose(), Times.Once);
    }

    [Fact]
    public void Dispose_NoOpWhenInnerNotDisposable()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        // Should not throw
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void IsEstimatedFillExchange_ReturnsFalse()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        sut.IsEstimatedFillExchange.Should().BeFalse();
    }

    [Fact]
    public void ExchangeName_DelegatesToInner()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        sut.ExchangeName.Should().Be("TestExchange");
    }
}
