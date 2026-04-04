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

    // ── NB1: Zero mark price guard ───────────────────────────────────────

    [Fact]
    public async Task PlaceMarketOrderAsync_ZeroMarkPrice_ReturnsFailure()
    {
        _mockInner.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.PlaceMarketOrderAsync("ETH", Side.Long, 300m, 5);

        result.Success.Should().BeFalse();
        result.OrderId.Should().BeEmpty();
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_ZeroMarkPrice_ReturnsFailure()
    {
        _mockInner.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Long, 0.5m, 5);

        result.Success.Should().BeFalse();
        result.OrderId.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosePositionAsync_ZeroMarkPrice_ReturnsFailure()
    {
        _mockInner.Setup(c => c.GetMarkPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0m);
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeFalse();
        result.OrderId.Should().BeEmpty();
    }

    // ── NB3: Constructor null guards ─────────────────────────────────────

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        var act = () => new DryRunConnectorWrapper(null!, _logger);

        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new DryRunConnectorWrapper(_mockInner.Object, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    // ── NB4: Short side slippage ─────────────────────────────────────────

    [Fact]
    public async Task ClosePositionAsync_ShortSide_AppliesInverseSlippage()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.ClosePositionAsync("ETH", Side.Short);

        result.Success.Should().BeTrue();
        result.FilledPrice.Should().Be(3000m * 1.001m);
    }

    [Fact]
    public async Task PlaceMarketOrderByQuantityAsync_ShortSide_ReturnsNegativeSlippage()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.PlaceMarketOrderByQuantityAsync("ETH", Side.Short, 0.5m, 5);

        result.Success.Should().BeTrue();
        result.FilledPrice.Should().Be(3000m * 0.999m);
    }

    // ── ClosePositionAsync returns 0 FilledQuantity (engine overrides) ────

    [Fact]
    public async Task ClosePositionAsync_ReturnsZeroFilledQuantity_EngineOverrides()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await sut.ClosePositionAsync("ETH", Side.Long);

        result.Success.Should().BeTrue();
        result.FilledQuantity.Should().Be(0m); // ExecutionEngine overrides with actual position quantity
    }

    // ── NB1: Verification methods ────────────────────────────────────────

    [Fact]
    public async Task CapturePositionSnapshotAsync_ReturnsEmptyDictionary()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await ((IPositionVerifiable)sut).CapturePositionSnapshotAsync();

        result.Should().NotBeNull();
        result!.Count.Should().Be(0);
    }

    [Fact]
    public async Task CheckPositionExistsAsync_ReturnsTrue()
    {
        var sut = new DryRunConnectorWrapper(_mockInner.Object, _logger);

        var result = await ((IPositionVerifiable)sut).CheckPositionExistsAsync("ETH", Side.Long);

        result.Should().Be(true);
    }
}
