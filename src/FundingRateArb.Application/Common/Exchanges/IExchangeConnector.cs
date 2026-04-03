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
    /// Captures a snapshot of current position sizes, keyed by (Symbol, Side).
    /// Used as a baseline before placing orders so that CheckPositionExistsAsync can
    /// distinguish new positions from pre-existing ones.
    /// </summary>
    Task<IReadOnlyDictionary<(string Symbol, string Side), decimal>?> CapturePositionSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<(string Symbol, string Side), decimal>?>(null);

    /// <summary>
    /// Single read-only check whether a position exists on the exchange for the given asset and side.
    /// When baseline is provided, returns true only if the position size increased vs baseline
    /// (indicating a new position was opened, not a pre-existing one).
    /// Returns true if found, false if not found, null if the check could not be performed.
    /// </summary>
    Task<bool?> CheckPositionExistsAsync(string asset, Side side,
        IReadOnlyDictionary<(string Symbol, string Side), decimal>? baseline = null,
        CancellationToken ct = default)
        => Task.FromResult<bool?>(null);
}
