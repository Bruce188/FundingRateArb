using System.Collections.Concurrent;
using Aster.Net.Enums;
using Aster.Net.Interfaces.Clients;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Aster DEX connector. Aster publishes 8-hour funding rates; all rates are
/// normalised to per-hour before being returned (<see cref="FundingRateDto.RatePerHour"/> = rawRate / 8).
/// Funding is settled periodically at 8-hour boundaries (00:00, 08:00, 16:00 UTC).
/// </summary>
public class AsterConnector : IExchangeConnector, IDisposable
{
    private readonly IAsterRestClient _restClient;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly ILogger<AsterConnector> _logger;
    private readonly IMarkPriceCache _markPriceCache;
    private readonly ConcurrentDictionary<string, int> _quantityPrecisionCache = new();
    private readonly ConcurrentDictionary<string, decimal> _tickSizeCache = new();
    private readonly SemaphoreSlim _symbolInfoLock = new(1, 1);
    private volatile bool _symbolInfoLoaded;

    public AsterConnector(
        IAsterRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<AsterConnector> logger,
        IMarkPriceCache markPriceCache)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
        _markPriceCache = markPriceCache;
        _logger = logger;
    }

    public string ExchangeName => "Aster";

    public bool IsEstimatedFillExchange => false;

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>UsdFuturesApi.ExchangeData.GetMarkPricesAsync</c> which returns the current
    /// mark price, index price and 8-hour funding rate for every symbol.
    /// Aster uses 8-hour funding intervals — rates are divided by 8 to normalise to per-hour.
    /// The original (undivided) value is preserved in <see cref="FundingRateDto.RawRate"/>.
    /// NextFundingTime from the API is carried through as NextSettlementUtc for settlement-aware accumulation.
    /// Symbol names are normalised: "ETHUSDT" → "ETH".
    /// </remarks>
    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var markPricesTask = pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.ExchangeData.GetMarkPricesAsync(token), ct).AsTask();
        var tickersTask = pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.ExchangeData.GetTickersAsync(token), ct).AsTask();

        await Task.WhenAll(markPricesTask, tickersTask);

        var markPrices = await markPricesTask;
        var tickers = await tickersTask;

        if (!markPrices.Success)
        {
            throw new InvalidOperationException(markPrices.Error?.Message ?? "Unknown error");
        }

        var volumeBySymbol = tickers.Success && tickers.Data is not null
            ? tickers.Data.ToDictionary(t => t.Symbol, t => t.QuoteVolume)
            : new Dictionary<string, decimal>();

        return markPrices.Data!
            .Select(mp => new FundingRateDto
            {
                ExchangeName = ExchangeName,
                Symbol = mp.Symbol.EndsWith("USDT") ? mp.Symbol[..^4] : mp.Symbol,
                RawRate = mp.FundingRate ?? 0m,
                RatePerHour = (mp.FundingRate ?? 0m) / 8m, // 8-hour rate normalised to per-hour
                MarkPrice = mp.MarkPrice,
                IndexPrice = mp.IndexPrice,
                Volume24hUsd = volumeBySymbol.GetValueOrDefault(mp.Symbol, 0m),
                NextSettlementUtc = mp.NextFundingTime,
            })
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sets leverage first, then places a market order. Size is expressed in USDC and converted
    /// to a quantity by dividing by the mark price.
    /// </remarks>
    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        var orderSide = side == Side.Long ? OrderSide.Buy : OrderSide.Sell;

        // Fetch mark price to compute quantity from the USDC notional size with leverage
        var markPrice = await GetMarkPriceAsync(asset, ct);

        // B1: Guard mark price = 0
        if (markPrice <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
        }

        var qtyPrecision = await GetSymbolQuantityPrecisionAsync(symbol, ct);
        var quantity = Math.Round(sizeUsdc * leverage / markPrice, qtyPrecision, MidpointRounding.ToZero);

        // B3: Zero-quantity guard
        if (quantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Calculated quantity is zero for {asset} (size={sizeUsdc}, leverage={leverage}, mark={markPrice})" };
        }

        // Min notional validation ($5 minimum)
        var notional = quantity * markPrice;
        if (notional < 5m)
        {
            return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Aster minimum $5.00" };
        }

        // Compute limit price with 0.5% slippage protection, rounded to tick size
        var tickSize = await GetTickSizeAsync(symbol, ct);
        var limitPrice = side == Side.Long
            ? RoundToTickSize(markPrice * 1.005m, tickSize)
            : RoundToTickSize(markPrice * 0.995m, tickSize);

        // B2: Abort order if SetLeverageAsync fails
        var leverageResult = await _restClient.FuturesApi.Account.SetLeverageAsync(symbol, leverage, null, ct);
        if (!leverageResult.Success)
        {
            return new OrderResultDto
            {
                Success = false,
                Error = $"Failed to set leverage to {leverage}x on {symbol}: {leverageResult.Error?.Message ?? "unknown"}. Aborting order."
            };
        }

        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                OrderType.Limit,
                quantity: quantity,
                price: limitPrice,
                positionSide: null,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: null,
                clientOrderId: null,
                stopPrice: null,
                closePosition: null,
                activationPrice: null,
                callbackRate: null,
                workingType: null,
                priceProtect: null,
                receiveWindow: null,
                ct: token),
            ct);

        if (!result.Success)
        {
            return new OrderResultDto
            {
                Success = false,
                Error = result.Error?.Message ?? "Unknown error",
            };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success = true,
            OrderId = order.Id.ToString(),
            FilledPrice = order.AveragePrice,
            FilledQuantity = order.QuantityFilled,
        };
    }

    /// <summary>
    /// Places a market order using a pre-computed quantity instead of computing from sizeUsdc.
    /// The quantity is rounded to the exchange's precision (may reduce, never increase).
    /// </summary>
    public async Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(
        string asset, Side side, decimal quantity, int leverage, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        var orderSide = side == Side.Long ? OrderSide.Buy : OrderSide.Sell;

        var markPrice = await GetMarkPriceAsync(asset, ct);

        if (markPrice <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
        }

        var qtyPrecision = await GetSymbolQuantityPrecisionAsync(symbol, ct);
        var roundedQuantity = Math.Round(quantity, qtyPrecision, MidpointRounding.ToZero);

        if (roundedQuantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Rounded quantity is zero for {asset} (quantity={quantity}, precision={qtyPrecision})" };
        }

        // Min notional validation ($5 minimum)
        var notional = roundedQuantity * markPrice;
        if (notional < 5m)
        {
            return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Aster minimum $5.00" };
        }

        // Compute limit price with 0.5% slippage protection, rounded to tick size
        var tickSize = await GetTickSizeAsync(symbol, ct);
        var limitPrice = side == Side.Long
            ? RoundToTickSize(markPrice * 1.005m, tickSize)
            : RoundToTickSize(markPrice * 0.995m, tickSize);

        var leverageResult = await _restClient.FuturesApi.Account.SetLeverageAsync(symbol, leverage, null, ct);
        if (!leverageResult.Success)
        {
            return new OrderResultDto
            {
                Success = false,
                Error = $"Failed to set leverage to {leverage}x on {symbol}: {leverageResult.Error?.Message ?? "unknown"}. Aborting order."
            };
        }

        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                OrderType.Limit,
                quantity: roundedQuantity,
                price: limitPrice,
                positionSide: null,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: null,
                clientOrderId: null,
                stopPrice: null,
                closePosition: null,
                activationPrice: null,
                callbackRate: null,
                workingType: null,
                priceProtect: null,
                receiveWindow: null,
                ct: token),
            ct);

        if (!result.Success)
        {
            return new OrderResultDto
            {
                Success = false,
                Error = result.Error?.Message ?? "Unknown error",
            };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success = true,
            OrderId = order.Id.ToString(),
            FilledPrice = order.AveragePrice,
            FilledQuantity = order.QuantityFilled,
        };
    }

    /// <summary>
    /// Returns the number of decimal places used for order quantities on Aster for the given asset.
    /// </summary>
    public async Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        return await GetSymbolQuantityPrecisionAsync(symbol, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Places a reduce-only market order on the opposite side to close the position.
    /// Long → Sell; Short → Buy.
    /// </remarks>
    public async Task<OrderResultDto> ClosePositionAsync(
        string asset, Side side, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        // To close a Long we sell; to close a Short we buy.
        var closeSide = side == Side.Long ? OrderSide.Sell : OrderSide.Buy;

        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        // Fetch current position to get explicit quantity (Aster API requires it)
        var posResult = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.GetPositionsAsync(symbol, ct: token), ct);
        if (!posResult.Success)
        {
            return new OrderResultDto { Success = false, Error = $"Failed to fetch position: {posResult.Error?.Message ?? "Unknown error"}" };
        }

        var pos = posResult.Data?.FirstOrDefault(p => p.Symbol == symbol && p.PositionAmount != 0);
        if (pos == null)
        {
            return new OrderResultDto { Success = false, Error = $"No open position for {symbol}" };
        }

        var quantity = Math.Abs(pos.PositionAmount);

        // B8: Use separate OrderClose pipeline (no circuit breaker)
        var orderPipeline = _pipelineProvider.GetPipeline("OrderClose");

        var result = await orderPipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol,
                closeSide,
                OrderType.Market,
                quantity: quantity,
                price: null,
                positionSide: null,
                timeInForce: null,
                reduceOnly: true,          // closes existing position
                clientOrderId: null,
                stopPrice: null,
                closePosition: null,
                activationPrice: null,
                callbackRate: null,
                workingType: null,
                priceProtect: null,
                receiveWindow: null,
                ct: token),
            ct);

        if (!result.Success)
        {
            return new OrderResultDto
            {
                Success = false,
                Error = result.Error?.Message ?? "Unknown error",
            };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success = true,
            OrderId = order.Id.ToString(),
            FilledPrice = order.AveragePrice,
            FilledQuantity = order.QuantityFilled,
        };
    }

    /// <inheritdoc />
    public async Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        return await _markPriceCache.GetOrRefreshAsync(ExchangeName, symbol, async token =>
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async t => await _restClient.FuturesApi.ExchangeData.GetMarkPricesAsync(t),
                token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error?.Message ?? "Unknown error");
            }

            var cache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var mp in result.Data!)
            {
                cache[mp.Symbol] = mp.MarkPrice;
            }

            return cache;
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Fetches the next funding settlement time from Aster's mark price endpoint.
    /// The API response includes NextFundingTime per symbol.
    /// Aster settles every 8 hours at 00:00, 08:00, 16:00 UTC.
    /// </remarks>
    public async Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var symbol = asset + "USDT";
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async t => await _restClient.FuturesApi.ExchangeData.GetMarkPricesAsync(t), ct);

            if (!result.Success || result.Data is null)
            {
                return ComputeNextSettlement8h();
            }

            var mp = result.Data.FirstOrDefault(m => m.Symbol == symbol);
            if (mp is null)
            {
                return ComputeNextSettlement8h();
            }

            return mp.NextFundingTime;
        }
        catch
        {
            return ComputeNextSettlement8h();
        }
    }

    /// <summary>Computes the next 8-hour settlement boundary (00:00, 08:00, 16:00 UTC).</summary>
    private static DateTime ComputeNextSettlement8h()
    {
        var now = DateTime.UtcNow;
        var flooredHour = now.Hour - (now.Hour % 8);
        return new DateTime(now.Year, now.Month, now.Day, flooredHour, 0, 0, DateTimeKind.Utc).AddHours(8);
    }

    public async Task<decimal?> GetRealizedPnlAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            return await PaginateIncomeHistoryAsync(asset + "USDT", IncomeType.RealizedPnl, from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aster GetRealizedPnlAsync failed for {Asset}", asset);
            return null;
        }
    }

    public async Task<decimal?> GetFundingPaymentsAsync(string asset, Side side, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            return await PaginateIncomeHistoryAsync(asset + "USDT", IncomeType.FundingFee, from, to, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Aster GetFundingPaymentsAsync failed for {Asset}", asset);
            return null;
        }
    }

    private async Task<decimal?> PaginateIncomeHistoryAsync(
        string symbol, IncomeType incomeType, DateTime from, DateTime to, CancellationToken ct)
    {
        const int pageSize = 1000;
        const int maxPages = 100;
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var cursor = from;
        var total = 0m;
        var page = 0;

        while (true)
        {
            if (++page > maxPages)
            {
                _logger.LogWarning(
                    "Aster PaginateIncomeHistoryAsync ({IncomeType}, {Symbol}) exceeded {MaxPages} pages — results may be incomplete",
                    incomeType, symbol, maxPages);
                break;
            }

            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetIncomeHistoryAsync(
                    symbol, incomeType, cursor, to, limit: pageSize, ct: token),
                ct);

            if (!result.Success || result.Data is null)
            {
                _logger.LogWarning("Aster GetIncomeHistoryAsync ({IncomeType}) failed: {Error}",
                    incomeType, result.Error?.Message);
                return null;
            }

            var entries = result.Data.ToList();
            total += entries.Sum(i => i.Income);

            if (entries.Count < pageSize)
            {
                break;
            }

            // Advance cursor past the last entry to fetch the next page
            cursor = entries[^1].Timestamp.AddMilliseconds(1);
        }

        return total;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Account.GetBalancesAsync(null, token),
            ct);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error?.Message ?? "Unknown error");
        }

        return result.Data!
            .Where(b => b.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.AvailableBalance);
    }

    public async Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetLeverageBracketsAsync(symbol, ct: token), ct);

            if (!result.Success || result.Data is null)
            {
                return null;
            }

            var symbolBracket = result.Data.FirstOrDefault();
            if (symbolBracket?.Brackets is null || symbolBracket.Brackets.Length == 0)
            {
                return null;
            }

            // The first bracket (lowest notional) has the highest allowed leverage
            return symbolBracket.Brackets.Max(b => b.InitialLeverage);
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> GetSymbolQuantityPrecisionAsync(string symbol, CancellationToken ct)
    {
        if (_quantityPrecisionCache.TryGetValue(symbol, out var cached))
        {
            return cached;
        }

        await EnsureSymbolInfoCachedAsync(symbol, ct);
        return _quantityPrecisionCache.GetValueOrDefault(symbol, 3);
    }

    private async Task<decimal> GetTickSizeAsync(string symbol, CancellationToken ct)
    {
        if (_tickSizeCache.TryGetValue(symbol, out var cached))
        {
            return cached;
        }

        await EnsureSymbolInfoCachedAsync(symbol, ct);
        return _tickSizeCache.GetValueOrDefault(symbol, 0.01m);
    }

    private async Task EnsureSymbolInfoCachedAsync(string symbol, CancellationToken ct)
    {
        // NB5: Double-checked locking to prevent duplicate exchange-info fetches on cold start
        if (_symbolInfoLoaded)
        {
            return;
        }

        await _symbolInfoLock.WaitAsync(ct);
        try
        {
            if (_symbolInfoLoaded)
            {
                return;
            }

            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.ExchangeData.GetExchangeInfoAsync(token), ct);

            if (result.Success && result.Data?.Symbols != null)
            {
                foreach (var s in result.Data.Symbols)
                {
                    _quantityPrecisionCache[s.Name] = s.QuantityPrecision;

                    // Extract tick size from PriceFilter if available, otherwise compute from PricePrecision
                    var priceFilter = s.PriceFilter;
                    if (priceFilter?.TickSize is > 0)
                    {
                        _tickSizeCache[s.Name] = priceFilter.TickSize;
                    }
                    else
                    {
                        // N4: Use integer exponentiation to avoid double-to-decimal cast error
                        decimal divisor = 1m;
                        for (int i = 0; i < s.PricePrecision; i++)
                        {
                            divisor *= 10m;
                        }
                        _tickSizeCache[s.Name] = 1m / divisor;
                    }
                }

                _symbolInfoLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch exchange info for symbol info lookup: {Error}", ex.Message);
        }
        finally
        {
            _symbolInfoLock.Release();
        }
    }

    public async Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
    {
        try
        {
            var symbol = asset + "USDT";
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.GetPositionsAsync(symbol, ct: token), ct);

            if (!result.Success)
            {
                return null;
            }

            var pos = result.Data?.FirstOrDefault(p => p.Symbol == symbol &&
                ((side == Side.Long && p.PositionAmount > 0) || (side == Side.Short && p.PositionAmount < 0)));
            return pos != null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<LeverageTier[]?> GetLeverageTiersAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var symbol = asset + "USDT";
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetLeverageBracketsAsync(symbol, ct: token), ct);

            if (!result.Success || result.Data is null)
                return null;

            var symbolBracket = result.Data.FirstOrDefault();
            if (symbolBracket?.Brackets is null || symbolBracket.Brackets.Length == 0)
                return null;

            return symbolBracket.Brackets
                .Select(b => new LeverageTier(
                    (decimal)b.Floor,
                    (decimal)b.Cap,
                    b.InitialLeverage,
                    b.MaintenanceMarginRatio))
                .OrderBy(t => t.NotionalFloor)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to fetch leverage tiers for {Asset}: {Error}", asset, ex.Message);
            return null;
        }
    }

    public async Task<MarginStateDto?> GetPositionMarginStateAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var symbol = asset + "USDT";
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Trading.GetPositionsAsync(symbol, ct: token), ct);

            if (!result.Success)
                return null;

            var pos = result.Data?.FirstOrDefault(p => p.Symbol == symbol && p.PositionAmount != 0);
            if (pos is null)
                return null;

            var marginUsed = pos.InitialMargin;
            var marginAvailable = pos.MaintenanceMargin > 0
                ? marginUsed - pos.MaintenanceMargin
                : 0m;

            return new MarginStateDto
            {
                MarginUsed = marginUsed,
                MarginAvailable = marginAvailable,
                LiquidationPrice = pos.LiquidationPrice,
                MarginUtilizationPct = marginUsed > 0 ? pos.MaintenanceMargin / marginUsed : 0m
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to fetch margin state for {Asset}: {Error}", asset, ex.Message);
            return null;
        }
    }

    private static decimal RoundToTickSize(decimal price, decimal tickSize)
    {
        if (tickSize <= 0)
        {
            return Math.Round(price, 2, MidpointRounding.AwayFromZero);
        }

        return Math.Round(price / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
    }
}
