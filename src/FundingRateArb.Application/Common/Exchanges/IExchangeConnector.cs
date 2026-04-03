using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;

namespace FundingRateArb.Application.Common.Exchanges;

public interface IExchangeConnector
{
    string ExchangeName { get; }

    /// <summary>
    /// Indicates whether this connector returns estimated fills (fire-and-forget tx submission).
    /// When true, the execution engine opens this leg first and verifies position existence
    /// before opening the reliable leg.
    /// </summary>
    bool IsEstimatedFillExchange { get; }

    Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default);
    Task<OrderResultDto> PlaceMarketOrderAsync(string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default);
    Task<OrderResultDto> ClosePositionAsync(string asset, Side side, CancellationToken ct = default);
    Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default);
    Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the maximum leverage supported by the exchange for the given asset.
    /// Returns null if max leverage cannot be determined (use configured leverage as fallback).
    /// </summary>
    Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default);

    /// <summary>
    /// Returns the next funding settlement time (UTC) for the given asset.
    /// Returns null if the settlement time cannot be determined.
    /// </summary>
    Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a position exists on the exchange for the given asset and side.
    /// Used by reconciliation to detect positions closed externally (exchange drift).
    /// Returns true if position exists, false if not, null if the check failed.
    /// </summary>
    Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
        => Task.FromResult<bool?>(null);

    /// <summary>
    /// Places a market order using a pre-computed quantity instead of computing from sizeUsdc.
    /// Used by ExecutionEngine for delta-neutral quantity coordination.
    /// </summary>
    Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(string asset, Side side, decimal quantity, int leverage, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support quantity-based orders");

    /// <summary>
    /// Returns the number of decimal places used for order quantities on this exchange for the given asset.
    /// Used by ExecutionEngine to determine the coarsest rounding grid across exchanges.
    /// </summary>
    Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support quantity precision query");
}

/// <summary>
/// Implemented by connectors that support position verification after fire-and-forget order submission.
/// Used by the execution engine to confirm a position actually opened on-chain before proceeding
/// with the second leg.
/// </summary>
public interface IPositionVerifiable
{
    /// <summary>
    /// Verifies that a position for the given asset and side actually exists on-chain.
    /// Retries up to 3 times with 2-second delays to allow for tx propagation.
    /// </summary>
    Task<bool> VerifyPositionOpenedAsync(string asset, Side side, CancellationToken ct = default);

    /// <summary>
    /// Single read-only check whether a position exists on the exchange for the given asset and side.
    /// Returns true if found, false if not found, null if the check could not be performed.
    /// </summary>
    Task<bool?> CheckPositionExistsAsync(string asset, Side side, CancellationToken ct = default)
        => Task.FromResult<bool?>(null);
}
