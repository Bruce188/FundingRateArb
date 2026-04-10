using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using HyperLiquid.Net.Enums;
using HyperLiquid.Net.Interfaces.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class HyperliquidConnector : IExchangeConnector, IDisposable
{
    private readonly IHyperLiquidRestClient _restClient;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly IMarkPriceCache _markPriceCache;
    private readonly ILogger<HyperliquidConnector> _logger;
    private readonly ConcurrentDictionary<string, int> _szDecimalsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _vaultAddress;

    private const decimal SlippagePct = 0.005m; // 0.5% max slippage

    public HyperliquidConnector(
        IHyperLiquidRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        IMarkPriceCache markPriceCache,
        string? vaultAddress = null,
        ILogger<HyperliquidConnector>? logger = null)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
        _markPriceCache = markPriceCache;
        _vaultAddress = vaultAddress;
        _logger = logger ?? NullLogger<HyperliquidConnector>.Instance;
    }

    public string ExchangeName => "Hyperliquid";

    public bool IsEstimatedFillExchange => false;

    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.ExchangeData.GetExchangeInfoAndTickersAsync(token),
            ct);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error!.ToString());
        }

        var now = DateTime.UtcNow;
        var nextSettlement = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);

        return result.Data.Tickers.Select(t => new FundingRateDto
        {
            ExchangeName = ExchangeName,
            Symbol = t.Symbol,
            // Hyperliquid funding rate is already per-hour — no conversion needed
            RawRate = t.FundingRate ?? 0m,
            RatePerHour = t.FundingRate ?? 0m,
            MarkPrice = t.MarkPrice,
            IndexPrice = t.OraclePrice ?? 0m,
            Volume24hUsd = t.NotionalVolume,
            NextSettlementUtc = nextSettlement,
        }).ToList();
    }

    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset,
        Side side,
        decimal sizeUsdc,
        int leverage,
        CancellationToken ct = default)
    {
        try
        {
            // Fetch current mark price for market order slippage parameter
            var markPrice = await GetMarkPriceAsync(asset, ct);

            // B1: Guard mark price = 0
            if (markPrice <= 0)
            {
                return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
            }

            // Set leverage before placing the order
            var pipeline = _pipelineProvider.GetPipeline("OrderExecution");
            await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.SetLeverageAsync(
                    asset, leverage, MarginType.Cross, null, null, token),
                ct);

            var sdkSide = side == Side.Long ? OrderSide.Buy : OrderSide.Sell;
            var szDecimals = _szDecimalsCache.TryGetValue(asset, out var cached) ? cached : 6;
            var quantity = Math.Round(sizeUsdc * leverage / markPrice, szDecimals, MidpointRounding.ToZero);

            // B3: Zero-quantity guard
            if (quantity <= 0)
            {
                return new OrderResultDto { Success = false, Error = $"Calculated quantity is zero for {asset} (size={sizeUsdc}, leverage={leverage}, mark={markPrice})" };
            }

            // Min notional validation ($10 minimum for Hyperliquid)
            var notional = quantity * markPrice;
            if (notional < 10m)
            {
                // Round up by one tick to clear minimum notional
                var tick = 1m / (decimal)Math.Pow(10, szDecimals);
                quantity += tick;
                notional = quantity * markPrice;
                if (notional < 10m)
                {
                    return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Hyperliquid minimum $10.00" };
                }
            }

            // B5: Slippage protection
            var limitPrice = side == Side.Long
                ? markPrice * (1 + SlippagePct)
                : markPrice * (1 - SlippagePct);

            var orderResult = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                    symbol: asset,
                    side: sdkSide,
                    orderType: OrderType.Market,
                    quantity: quantity,
                    price: limitPrice,
                    timeInForce: TimeInForce.ImmediateOrCancel,
                    reduceOnly: false,
                    clientOrderId: null,
                    triggerPrice: null,
                    tpSlType: null,
                    tpSlGrouping: null,
                    vaultAddress: _vaultAddress,
                    expireAfter: null,
                    ct: token),
                ct);

            if (!orderResult.Success)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = orderResult.Error!.ToString(),
                };
            }

            var data = orderResult.Data;
            return new OrderResultDto
            {
                Success = true,
                OrderId = data.OrderId.ToString(),
                FilledQuantity = data.FilledQuantity ?? 0m,
                FilledPrice = data.AveragePrice ?? 0m,
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Places a market order using a pre-computed quantity instead of computing from sizeUsdc.
    /// The quantity is rounded to szDecimals (may reduce, never increase).
    /// </summary>
    public async Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(
        string asset, Side side, decimal quantity, int leverage, CancellationToken ct = default)
    {
        try
        {
            var markPrice = await GetMarkPriceAsync(asset, ct);

            if (markPrice <= 0)
            {
                return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
            }

            // Set leverage before placing the order
            var pipeline = _pipelineProvider.GetPipeline("OrderExecution");
            await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.SetLeverageAsync(
                    asset, leverage, MarginType.Cross, null, null, token),
                ct);

            var sdkSide = side == Side.Long ? OrderSide.Buy : OrderSide.Sell;
            var szDecimals = _szDecimalsCache.TryGetValue(asset, out var cached) ? cached : 6;
            var roundedQuantity = Math.Round(quantity, szDecimals, MidpointRounding.ToZero);

            if (roundedQuantity <= 0)
            {
                return new OrderResultDto { Success = false, Error = $"Rounded quantity is zero for {asset} (quantity={quantity}, szDecimals={szDecimals})" };
            }

            // Min notional validation ($10 minimum for Hyperliquid)
            var notional = roundedQuantity * markPrice;
            if (notional < 10m)
            {
                // Round up by one tick to clear minimum notional
                var tick = 1m / (decimal)Math.Pow(10, szDecimals);
                var originalQuantity = roundedQuantity;
                roundedQuantity += tick;
                notional = roundedQuantity * markPrice;
                if (notional < 10m)
                {
                    return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Hyperliquid minimum $10.00" };
                }
                _logger.LogWarning(
                    "Min-notional bump activated: quantity adjusted from {From} to {To} for {Asset}",
                    originalQuantity, roundedQuantity, asset);
            }

            var limitPrice = side == Side.Long
                ? markPrice * (1 + SlippagePct)
                : markPrice * (1 - SlippagePct);

            var orderResult = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                    symbol: asset,
                    side: sdkSide,
                    orderType: OrderType.Market,
                    quantity: roundedQuantity,
                    price: limitPrice,
                    timeInForce: TimeInForce.ImmediateOrCancel,
                    reduceOnly: false,
                    clientOrderId: null,
                    triggerPrice: null,
                    tpSlType: null,
                    tpSlGrouping: null,
                    vaultAddress: _vaultAddress,
                    expireAfter: null,
                    ct: token),
                ct);

            if (!orderResult.Success)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = orderResult.Error!.ToString(),
                };
            }

            var data = orderResult.Data;
            return new OrderResultDto
            {
                Success = true,
                OrderId = data.OrderId.ToString(),
                FilledQuantity = data.FilledQuantity ?? 0m,
                FilledPrice = data.AveragePrice ?? 0m,
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Returns the number of decimal places used for order quantities on Hyperliquid for the given asset.
    /// </summary>
    public async Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
    {
        // Ensure szDecimals cache is populated by fetching exchange info
        if (!_szDecimalsCache.TryGetValue(asset, out var cached))
        {
            // Trigger cache population via GetMarkPriceAsync which loads exchange info
            await GetMarkPriceAsync(asset, ct);
            cached = _szDecimalsCache.TryGetValue(asset, out var refreshed) ? refreshed : 6;
        }
        return cached;
    }

    public async Task<OrderResultDto> ClosePositionAsync(
        string asset,
        Side side,
        CancellationToken ct = default)
    {
        var markPrice = await GetMarkPriceAsync(asset, ct);

        // Try to get actual position size from account info
        // GetPositionQuantityAsync returns signed qty (positive = long, negative = short);
        // take Abs for close size since the exchange close API expects unsigned quantity
        var (rawQuantity, apiConfirmed) = await GetPositionQuantityAsync(asset, ct);
        var quantity = Math.Abs(rawQuantity);
        if (quantity <= 0)
        {
            var error = apiConfirmed
                ? "Position quantity is zero — API confirms no open position"
                : "Position quantity is zero — API call failed or returned transient result";
            return new OrderResultDto { Success = false, Error = error };
        }

        // To close: place opposite side with reduceOnly=true
        var closingSide = side == Side.Long ? OrderSide.Sell : OrderSide.Buy;

        // B5: Slippage protection for close
        var limitPrice = closingSide == OrderSide.Buy
            ? markPrice * (1 + SlippagePct)
            : markPrice * (1 - SlippagePct);

        // B8: Use separate OrderClose pipeline (no circuit breaker)
        var pipeline = _pipelineProvider.GetPipeline("OrderClose");
        var orderResult = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol: asset,
                side: closingSide,
                orderType: OrderType.Market,
                quantity: quantity,
                price: limitPrice,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: true,
                clientOrderId: null,
                triggerPrice: null,
                tpSlType: null,
                tpSlGrouping: null,
                vaultAddress: _vaultAddress,
                expireAfter: null,
                ct: token),
            ct);

        if (!orderResult.Success)
        {
            return new OrderResultDto
            {
                Success = false,
                Error = orderResult.Error!.ToString(),
            };
        }

        var data = orderResult.Data;
        return new OrderResultDto
        {
            Success = true,
            OrderId = data.OrderId.ToString(),
            FilledQuantity = data.FilledQuantity ?? 0m,
            FilledPrice = data.AveragePrice ?? 0m,
        };
    }

    public async Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        return await _markPriceCache.GetOrRefreshAsync(ExchangeName, asset, async token =>
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async t => await _restClient.FuturesApi.ExchangeData.GetExchangeInfoAndTickersAsync(t),
                token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error!.ToString());
            }

            if (result.Data.ExchangeInfo?.Symbols != null)
            {
                foreach (var s in result.Data.ExchangeInfo.Symbols)
                {
                    _szDecimalsCache[s.Name] = s.QuantityDecimals;
                }
            }

            var cache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in result.Data.Tickers)
            {
                cache[t.Symbol] = t.MarkPrice;
            }

            return cache;
        }, ct);
    }

    public async Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.ExchangeData.GetExchangeInfoAndTickersAsync(token),
                ct);

            if (!result.Success || result.Data.ExchangeInfo?.Symbols is null)
            {
                return null;
            }

            var symbol = result.Data.ExchangeInfo.Symbols
                .FirstOrDefault(s => s.Name.Equals(asset, StringComparison.OrdinalIgnoreCase));

            return symbol?.MaxLeverage;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    /// <remarks>Hyperliquid settles funding hourly on the hour.</remarks>
    public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        return Task.FromResult<DateTime?>(nextHour);
    }

    public async Task<decimal?> GetRealizedPnlAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.GetUserTradesByTimeAsync(
                    from, to, aggregateByTime: null, address: _vaultAddress, ct: token),
                ct);

            if (!result.Success || result.Data is null)
            {
                _logger.LogWarning("Hyperliquid GetUserTradesByTimeAsync failed: {Error}", result.Error?.ToString());
                return null;
            }

            var trades = result.Data.ToList();
            if (trades.Count > 5000)
            {
                _logger.LogWarning(
                    "Hyperliquid returned {Count} trades for {Asset} — response may be truncated",
                    trades.Count, asset);
            }

            var pnl = trades
                .Where(t => t.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase)
                         || t.ExchangeSymbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
                .Sum(t => t.ClosedPnl ?? 0m);

            return pnl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hyperliquid GetRealizedPnlAsync failed for {Asset}", asset);
            return null;
        }
    }

    public async Task<decimal?> GetFundingPaymentsAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetFundingHistoryAsync(
                    from, to, address: _vaultAddress, ct: token),
                ct);

            if (!result.Success || result.Data is null)
            {
                _logger.LogWarning("Hyperliquid GetFundingHistoryAsync failed: {Error}", result.Error?.ToString());
                return null;
            }

            var entries = result.Data.ToList();
            if (entries.Count > 5000)
            {
                _logger.LogWarning(
                    "Hyperliquid returned {Count} funding entries for {Asset} — response may be truncated",
                    entries.Count, asset);
            }

            var funding = entries
                .Where(f => f.Data.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
                .Sum(f => f.Data.Usdc);

            return funding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hyperliquid GetFundingPaymentsAsync failed for {Asset}", asset);
            return null;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        // Perps balance (withdrawable)
        var perpsResult = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Account.GetAccountInfoAsync(_vaultAddress, null, token),
            ct);

        if (!perpsResult.Success)
        {
            throw new InvalidOperationException(perpsResult.Error!.ToString());
        }

        var perpsBalance = perpsResult.Data.Withdrawable;

        // Spot balance (USDC available) — unified account may hold funds in spot
        var spotResult = await pipeline.ExecuteAsync(
            async token => await _restClient.SpotApi.Account.GetBalancesAsync(_vaultAddress, token),
            ct);

        decimal spotUsdc = 0m;
        if (spotResult.Success && spotResult.Data != null)
        {
            var usdcBalance = spotResult.Data.FirstOrDefault(b =>
                string.Equals(b.Asset, "USDC", StringComparison.OrdinalIgnoreCase));
            if (usdcBalance != null)
            {
                spotUsdc = usdcBalance.Total - usdcBalance.Hold;
            }
        }

        return perpsBalance + spotUsdc;
    }

    /// <summary>
    /// Checks whether a position exists on this exchange for the given asset and side.
    /// Returns true if an open position matching the requested direction exists,
    /// false if confirmed absent, null if the API call failed.
    /// </summary>
    public async Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
    {
        var (rawQty, confirmed) = await GetPositionQuantityAsync(asset, ct);
        if (!confirmed)
        {
            return null;
        }
        return (side == Side.Long && rawQty > 0) || (side == Side.Short && rawQty < 0);
    }

    /// <summary>
    /// Fetches the actual open position quantity for the given asset from account info.
    /// Returns the raw signed quantity (positive = long, negative = short) and whether
    /// the API call succeeded. apiConfirmed=true means the API successfully returned data
    /// (position genuinely doesn't exist if qty is 0). apiConfirmed=false means the
    /// API call failed or returned an unexpected result (transient — allow retry).
    /// </summary>
    public async Task<LeverageTier[]?> GetLeverageTiersAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.ExchangeData.GetExchangeInfoAndTickersAsync(token),
                ct);

            if (!result.Success || result.Data.ExchangeInfo?.Symbols is null)
            {
                return null;
            }

            var symbol = result.Data.ExchangeInfo.Symbols
                .FirstOrDefault(s => s.Name.Equals(asset, StringComparison.OrdinalIgnoreCase));

            if (symbol is null || symbol.MaxLeverage <= 0)
            {
                return null;
            }

            return new[]
            {
                new LeverageTier(0m, decimal.MaxValue, symbol.MaxLeverage, 1m / symbol.MaxLeverage)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to fetch leverage tiers for {Asset}: {Error}", asset, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Computes margin utilization with a zero-denominator safeguard. When the account
    /// value collapses to zero with non-zero margin committed (cross-margin account
    /// fully consumed by adverse PnL/funding), returns 1m (100% utilized) so the alert
    /// threshold fires. Reporting 0% would mask the catastrophic state at exactly the
    /// moment the alert is most needed. Exposed as internal so unit tests can verify the
    /// branch matrix without mocking the Hyperliquid SDK pipeline.
    /// </summary>
    internal static decimal ComputeMarginUtilization(decimal accountValue, decimal totalMarginUsed)
    {
        if (accountValue > 0m)
        {
            return totalMarginUsed / accountValue;
        }
        return totalMarginUsed > 0m ? 1m : 0m;
    }

    public async Task<MarginStateDto?> GetPositionMarginStateAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetAccountInfoAsync(_vaultAddress, null, token),
                ct);

            if (!result.Success || result.Data is null)
            {
                return null;
            }

            var accountInfo = result.Data;
            var marginSummary = accountInfo.MarginSummary;

            // Account-level totals (falls back to 0 if the summary is not populated)
            var accountValue = marginSummary?.AccountValue ?? 0m;
            var totalMarginUsed = marginSummary?.TotalMarginUsed ?? 0m;
            var withdrawable = accountInfo.Withdrawable;

            // Find the matching position to get per-asset liquidation/margin data
            var positionWrapper = accountInfo.Positions?
                .FirstOrDefault(p => string.Equals(p.Position?.Symbol, asset, StringComparison.OrdinalIgnoreCase));
            var position = positionWrapper?.Position;

            var utilization = ComputeMarginUtilization(accountValue, totalMarginUsed);

            if (position is null)
            {
                // No open position — surface account-level margin only.
                return new MarginStateDto
                {
                    MarginUsed = totalMarginUsed,
                    MarginAvailable = withdrawable,
                    LiquidationPrice = null,
                    MarginUtilizationPct = utilization,
                };
            }

            return new MarginStateDto
            {
                MarginUsed = position.MarginUsed ?? 0m,
                MarginAvailable = withdrawable,
                LiquidationPrice = position.LiquidationPrice,
                MarginUtilizationPct = utilization,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to fetch Hyperliquid margin state for {Asset}: {Error}", asset, ex.Message);
            return null;
        }
    }

    private async Task<(decimal Quantity, bool ApiConfirmed)> GetPositionQuantityAsync(string asset, CancellationToken ct)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetAccountInfoAsync(_vaultAddress, null, token),
                ct);

            if (!result.Success || result.Data.Positions is null)
            {
                // API call failed or returned null — transient, allow retry
                return (0m, false);
            }

            var position = result.Data.Positions
                .FirstOrDefault(p => p.Position?.Symbol == asset);

            var qty = position?.Position?.PositionQuantity ?? 0m;
            // API succeeded — return raw signed quantity (positive = long, negative = short)
            return (qty, true);
        }
        catch
        {
            // Exception during API call — transient, allow retry
            return (0m, false);
        }
    }
}
