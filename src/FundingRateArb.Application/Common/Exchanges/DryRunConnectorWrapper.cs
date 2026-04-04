using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace FundingRateArb.Application.Common.Exchanges;

/// <summary>
/// Decorator that wraps a real exchange connector to simulate order fills using mark prices.
/// Read-only methods delegate to the inner connector; write methods return simulated fills.
/// </summary>
public sealed class DryRunConnectorWrapper : IExchangeConnector, IPositionVerifiable, IDisposable
{
    private readonly IExchangeConnector _inner;
    private readonly ILogger _logger;

    public DryRunConnectorWrapper(IExchangeConnector inner, ILogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Read-only: delegate to inner ──────────────────────────────────────

    public string ExchangeName => _inner.ExchangeName;

    public bool IsEstimatedFillExchange => false;

    public Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
        => _inner.GetFundingRatesAsync(ct);

    public Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
        => _inner.GetMarkPriceAsync(asset, ct);

    public Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
        => _inner.GetAvailableBalanceAsync(ct);

    public Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
        => _inner.GetMaxLeverageAsync(asset, ct);

    public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
        => _inner.GetNextFundingTimeAsync(asset, ct);

    public Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
        => _inner.HasOpenPositionAsync(asset, side, ct);

    public Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
        => _inner.GetQuantityPrecisionAsync(asset, ct);

    // ── Write: simulated fills ────────────────────────────────────────────

    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        var markPrice = await _inner.GetMarkPriceAsync(asset, ct);
        var quantity = sizeUsdc / markPrice;
        var fillPrice = side == Side.Long ? markPrice * 1.001m : markPrice * 0.999m;

        _logger.LogInformation("[DRY-RUN] PlaceMarketOrder {Asset} {Side} size={Size} qty={Qty} fill={Fill}",
            asset, side, sizeUsdc, quantity, fillPrice);

        return new OrderResultDto
        {
            Success = true,
            FilledQuantity = quantity,
            FilledPrice = fillPrice,
            OrderId = $"DRY-{Guid.NewGuid().ToString("N")[..8]}",
        };
    }

    public async Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(
        string asset, Side side, decimal quantity, int leverage, CancellationToken ct = default)
    {
        var markPrice = await _inner.GetMarkPriceAsync(asset, ct);
        var fillPrice = side == Side.Long ? markPrice * 1.001m : markPrice * 0.999m;

        _logger.LogInformation("[DRY-RUN] PlaceMarketOrderByQuantity {Asset} {Side} qty={Qty} fill={Fill}",
            asset, side, quantity, fillPrice);

        return new OrderResultDto
        {
            Success = true,
            FilledQuantity = quantity,
            FilledPrice = fillPrice,
            OrderId = $"DRY-{Guid.NewGuid().ToString("N")[..8]}",
        };
    }

    public async Task<OrderResultDto> ClosePositionAsync(
        string asset, Side side, CancellationToken ct = default)
    {
        var markPrice = await _inner.GetMarkPriceAsync(asset, ct);
        // Inverse slippage on close: closing a long sells at slightly lower, closing a short buys at slightly higher
        var fillPrice = side == Side.Long ? markPrice * 0.999m : markPrice * 1.001m;

        _logger.LogInformation("[DRY-RUN] ClosePosition {Asset} {Side} fill={Fill}",
            asset, side, fillPrice);

        return new OrderResultDto
        {
            Success = true,
            FilledQuantity = 0,
            FilledPrice = fillPrice,
            OrderId = $"DRY-{Guid.NewGuid().ToString("N")[..8]}",
        };
    }

    // ── Verification: always succeed ──────────────────────────────────────

    public Task<bool> VerifyPositionOpenedAsync(string asset, Side side, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<IReadOnlyDictionary<(string Symbol, string Side), decimal>?> CapturePositionSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<(string Symbol, string Side), decimal>?>(
            new Dictionary<(string Symbol, string Side), decimal>());

    public Task<bool?> CheckPositionExistsAsync(
        string asset, Side side,
        IReadOnlyDictionary<(string Symbol, string Side), decimal>? baseline = null,
        CancellationToken ct = default)
        => Task.FromResult<bool?>(true);

    // ── Disposal ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
