using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;

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

    /// <summary>
    /// Returns true when this connector has the credentials necessary to make
    /// authenticated API calls. Callers may skip operations that require credentials
    /// when this returns false, rather than letting the SDK throw.
    /// </summary>
    bool HasCredentials => true;

    /// <summary>
    /// Hardcoded per-connector capability declaring whether the connector speaks perp futures or spot.
    /// Default <see cref="ExchangeMarketType.Perp"/> — opt-in override per connector.
    /// </summary>
    ExchangeMarketType MarketType => ExchangeMarketType.Perp;

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
    /// Returns the exchange-reported realized PnL for the given asset over the time window.
    /// Returns null if the exchange does not support PnL queries or the API call fails.
    /// </summary>
    Task<decimal?> GetRealizedPnlAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default)
        => Task.FromResult<decimal?>(null);

    /// <summary>
    /// Returns the exchange-reported funding payments for the given asset over the time window.
    /// Returns null if the exchange does not support funding payment queries or the API call fails.
    /// </summary>
    Task<decimal?> GetFundingPaymentsAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default)
        => Task.FromResult<decimal?>(null);

    /// <summary>
    /// Places a market order using a pre-computed quantity instead of computing from sizeUsdc.
    /// Used by ExecutionEngine for delta-neutral quantity coordination.
    ///
    /// <paramref name="clientOrderId"/> (optional, default null) is the deterministic
    /// idempotency key from <c>OrderIdGenerator.For</c>. When the exchange recognises a
    /// previously-submitted ID, it returns the existing order rather than creating a new one.
    /// Connectors that don't support client order ids (e.g. Lighter — on-chain) ignore the
    /// argument; callers are not required to pass it.
    /// </summary>
    Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(string asset, Side side, decimal quantity, int leverage, string? clientOrderId = null, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support quantity-based orders");

    /// <summary>
    /// Returns the number of decimal places used for order quantities on this exchange for the given asset.
    /// Used by ExecutionEngine to determine the coarsest rounding grid across exchanges.
    /// </summary>
    Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not support quantity precision query");

    Task<LeverageTier[]?> GetLeverageTiersAsync(string asset, CancellationToken ct = default)
        => Task.FromResult<LeverageTier[]?>(null);

    Task<MarginStateDto?> GetPositionMarginStateAsync(string asset, CancellationToken ct = default)
        => Task.FromResult<MarginStateDto?>(null);

    /// <summary>
    /// Returns all open positions reported by the exchange for the connected account.
    /// Tuple per position: (Asset symbol on the exchange, Side, position size in base-asset units).
    /// Returns null when the exchange or this connector does not support the enumeration; reconciliation
    /// degrades gracefully on null. Used by the periodic reconciliation job to detect orphan positions
    /// — open on the exchange but not in the DB.
    /// </summary>
    Task<IReadOnlyList<(string Asset, Side Side, decimal Size)>?> GetAllOpenPositionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string, Side, decimal)>?>(null);

    /// <summary>
    /// Returns the exchange-reported commission income (sum of trading fees) over the time window.
    /// Returns null when the exchange does not expose commission income or the API call fails.
    /// Used by the periodic reconciliation job to compare DB-summed entry+exit fees vs the exchange's
    /// reported total.
    /// </summary>
    Task<decimal?> GetCommissionIncomeAsync(DateTime from, DateTime to, CancellationToken ct = default)
        => Task.FromResult<decimal?>(null);

    /// <summary>
    /// Returns an estimated avg-fill price + slippage for the requested quantity by walking the
    /// order ladder. Default returns <c>null</c> — connectors opt-in by overriding. The depth gate
    /// in <c>ExecutionEngine</c> SKIPS the gate when this returns <c>null</c>.
    /// </summary>
    /// <returns>
    /// <c>null</c> — depth could not be determined (degrade gracefully). Gate is skipped.
    /// <c>OrderbookDepthSnapshot { Source = Insufficient }</c> — ladder lacked depth. Gate REJECTS.
    /// Otherwise — gate evaluates against <c>IPreflightSlippageGuard.GetCurrentCap</c>.
    /// </returns>
    Task<OrderbookDepthSnapshot?> GetOrderbookDepthAsync(string asset, Side side, decimal quantity, CancellationToken ct = default)
        => Task.FromResult<OrderbookDepthSnapshot?>(null);
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

/// <summary>
/// Implemented by connectors that can provide actual entry prices after position verification.
/// Used by the execution engine to reconcile estimated fill prices with exchange-reported
/// average entry prices.
/// </summary>
public interface IEntryPriceReconcilable
{
    /// <summary>
    /// Returns the actual entry price for the given asset and side from the exchange API.
    /// Returns null if the price cannot be determined (position not found, API failure).
    /// </summary>
    Task<decimal?> GetActualEntryPriceAsync(string asset, Side side, CancellationToken ct = default);
}

/// <summary>
/// Implemented by connectors that support setting an expected fill quantity
/// to improve position verification when baseline size matches current size.
/// </summary>
public interface IExpectedFillAware
{
    /// <summary>Sets the expected fill quantity before verification.</summary>
    void SetExpectedFillQuantity(decimal quantity);

    /// <summary>Clears the expected fill quantity after verification completes.</summary>
    void ClearExpectedFillQuantity();
}
