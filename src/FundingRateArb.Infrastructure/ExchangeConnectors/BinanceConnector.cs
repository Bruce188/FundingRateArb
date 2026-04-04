using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Interfaces.Clients.UsdFuturesApi;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Binance Futures connector. Binance publishes 8-hour funding rates; all rates are
/// normalised to per-hour before being returned (<see cref="FundingRateDto.RatePerHour"/> = rawRate / 8).
/// Funding is settled periodically at 8-hour boundaries (00:00, 08:00, 16:00 UTC).
/// </summary>
public class BinanceConnector : IExchangeConnector, IDisposable
{
    private readonly IBinanceRestClient _restClient;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly ILogger<BinanceConnector> _logger;
    private readonly IMarkPriceCache _markPriceCache;
    private readonly ConcurrentDictionary<string, int> _quantityPrecisionCache = new();
    private readonly ConcurrentDictionary<string, decimal> _tickSizeCache = new();
    private readonly ConcurrentDictionary<string, DateTime?> _fundingTimeCache = new();
    private long _fundingTimeCacheExpiryTicks;
    private readonly SemaphoreSlim _symbolInfoLock = new(1, 1);
    private volatile bool _symbolInfoLoaded;
    private int _positionModeChecked;
    private long _symbolInfoFailedUntilTicks = DateTime.MinValue.Ticks;

    public BinanceConnector(
        IBinanceRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<BinanceConnector> logger,
        IMarkPriceCache markPriceCache)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
        _markPriceCache = markPriceCache;
        _logger = logger;
    }

    public string ExchangeName => "Binance";

    public bool IsEstimatedFillExchange => false;

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>UsdFuturesApi.ExchangeData.GetMarkPricesAsync</c> which returns the current
    /// mark price, index price and 8-hour funding rate for every symbol.
    /// Binance uses 8-hour funding intervals — rates are divided by 8 to normalise to per-hour.
    /// The original (undivided) value is preserved in <see cref="FundingRateDto.RawRate"/>.
    /// NextFundingTime from the API is carried through as NextSettlementUtc for settlement-aware accumulation.
    /// Symbol names are normalised: "ETHUSDT" → "ETH".
    /// </remarks>
    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var markPricesTask = pipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.ExchangeData.GetMarkPricesAsync(token), ct).AsTask();
        var tickersTask = pipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.ExchangeData.GetTickersAsync(token), ct).AsTask();
        // fundingInfoTask runs concurrently but is NOT in Task.WhenAll to prevent
        // its timeout from crashing the entire fetch (B2 fix). Consumed separately below.
        var fundingInfoTask = pipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.ExchangeData.GetFundingInfoAsync(token), ct)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), ct);

        await Task.WhenAll(markPricesTask, tickersTask);

        var markPrices = await markPricesTask;

        if (!markPrices.Success)
        {
            throw new InvalidOperationException(markPrices.Error?.Message ?? "Unknown error");
        }

        Dictionary<string, decimal> volumeBySymbol;
        try
        {
            var tickers = await tickersTask;
            volumeBySymbol = tickers.Success && tickers.Data is not null
                ? tickers.Data.DistinctBy(t => t.Symbol).ToDictionary(t => t.Symbol, t => t.QuoteVolume)
                : new Dictionary<string, decimal>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Binance tickers; falling back to empty volume map");
            volumeBySymbol = new Dictionary<string, decimal>();
        }

        // Build funding interval lookup from the dedicated funding info endpoint
        Dictionary<string, int> intervalBySymbol;
        try
        {
            var fundingInfo = await fundingInfoTask;
            intervalBySymbol = fundingInfo.Success && fundingInfo.Data is not null
                ? fundingInfo.Data
                    .Where(fi => fi.FundingIntervalHours > 0)
                    .DistinctBy(fi => fi.Symbol)
                    .ToDictionary(fi => fi.Symbol, fi => fi.FundingIntervalHours)
                : new Dictionary<string, int>();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Binance funding info; falling back to default intervals");
            intervalBySymbol = new Dictionary<string, int>();
        }

        return markPrices.Data!
            .Select(mp =>
            {
                var intervalHours = intervalBySymbol.GetValueOrDefault(mp.Symbol, 8);
                return new FundingRateDto
                {
                    ExchangeName = ExchangeName,
                    Symbol = mp.Symbol.EndsWith("USDT") ? mp.Symbol[..^4] : mp.Symbol,
                    RawRate = mp.FundingRate ?? 0m,
                    RatePerHour = (mp.FundingRate ?? 0m) / intervalHours,
                    MarkPrice = mp.MarkPrice,
                    IndexPrice = mp.IndexPrice,
                    Volume24hUsd = volumeBySymbol.GetValueOrDefault(mp.Symbol, 0m),
                    NextSettlementUtc = mp.NextFundingTime,
                    DetectedFundingIntervalHours = intervalBySymbol.TryGetValue(mp.Symbol, out var detected) ? detected : null,
                };
            })
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sets leverage first, then places a limit IOC order with 0.5% slippage.
    /// Size is expressed in USDC and converted to a quantity by dividing by the mark price.
    /// </remarks>
    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        if (leverage < 1 || leverage > 125)
        {
            return new OrderResultDto { Success = false, Error = $"Invalid leverage {leverage} (must be 1-125)" };
        }

        var symbol = asset + "USDT";
        var orderSide = side == Side.Long ? OrderSide.Buy : OrderSide.Sell;

        // Fetch mark price to compute quantity from the USDC notional size with leverage
        var markPrice = await GetMarkPriceAsync(asset, ct);

        if (markPrice <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
        }

        var qtyPrecision = await GetSymbolQuantityPrecisionAsync(symbol, ct);
        var quantity = Math.Round(sizeUsdc * leverage / markPrice, qtyPrecision, MidpointRounding.ToZero);

        if (quantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Calculated quantity is zero for {asset} (size={sizeUsdc}, leverage={leverage}, mark={markPrice})" };
        }

        // Min notional validation ($5 minimum)
        var notional = quantity * markPrice;
        if (notional < 5m)
        {
            return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Binance minimum $5.00" };
        }

        // Compute limit price with 0.5% slippage protection, rounded to tick size
        var tickSize = await GetTickSizeAsync(symbol, ct);
        var limitPrice = side == Side.Long
            ? RoundToTickSize(markPrice * 1.005m, tickSize)
            : RoundToTickSize(markPrice * 0.995m, tickSize);

        // Abort order if ChangeInitialLeverageAsync fails
        var sdkPipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var leverageResult = await sdkPipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: token), ct);
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
            async token => await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                FuturesOrderType.Limit,
                quantity: quantity,
                price: limitPrice,
                positionSide: null,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: null,
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
        if (leverage < 1 || leverage > 125)
        {
            return new OrderResultDto { Success = false, Error = $"Invalid leverage {leverage} (must be 1-125)" };
        }

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
            return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Binance minimum $5.00" };
        }

        // Compute limit price with 0.5% slippage protection, rounded to tick size
        var tickSize = await GetTickSizeAsync(symbol, ct);
        var limitPrice = side == Side.Long
            ? RoundToTickSize(markPrice * 1.005m, tickSize)
            : RoundToTickSize(markPrice * 0.995m, tickSize);

        var sdkPipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var leverageResult = await sdkPipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.Account.ChangeInitialLeverageAsync(symbol, leverage, ct: token), ct);
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
            async token => await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                FuturesOrderType.Limit,
                quantity: roundedQuantity,
                price: limitPrice,
                positionSide: null,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: null,
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
    /// Returns the number of decimal places used for order quantities on Binance for the given asset.
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

        // Fetch current position to get explicit quantity
        var posResult = await pipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: token), ct);
        if (!posResult.Success)
        {
            return new OrderResultDto { Success = false, Error = $"Failed to fetch position: {posResult.Error?.Message ?? "Unknown error"}" };
        }

        var pos = posResult.Data?.FirstOrDefault(p => p.Symbol == symbol &&
            ((side == Side.Long && p.Quantity > 0) || (side == Side.Short && p.Quantity < 0)));
        if (pos == null)
        {
            return new OrderResultDto { Success = false, Error = $"No open position for {symbol}" };
        }

        var quantity = Math.Abs(pos.Quantity);

        // Use separate OrderClose pipeline (no circuit breaker)
        var orderPipeline = _pipelineProvider.GetPipeline("OrderClose");

        var result = await orderPipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                symbol,
                closeSide,
                FuturesOrderType.Market,
                quantity: quantity,
                price: null,
                positionSide: null,
                timeInForce: null,
                reduceOnly: true,
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
                async t => await _restClient.UsdFuturesApi.ExchangeData.GetMarkPricesAsync(t),
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
    /// Fetches the next funding settlement time from Binance's mark price endpoint.
    /// The API response includes NextFundingTime per symbol.
    /// Binance settles every 8 hours at 00:00, 08:00, 16:00 UTC.
    /// </remarks>
    public async Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var symbol = asset + "USDT";

            // Use cached funding times when available (populated alongside mark prices)
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _fundingTimeCacheExpiryTicks) &&
                _fundingTimeCache.TryGetValue(symbol, out var cachedTime))
            {
                return cachedTime;
            }

            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async t => await _restClient.UsdFuturesApi.ExchangeData.GetMarkPricesAsync(t), ct);

            if (!result.Success || result.Data is null)
            {
                return ComputeNextSettlement8h();
            }

            // Cache all funding times with a short TTL
            foreach (var item in result.Data)
            {
                _fundingTimeCache[item.Symbol] = item.NextFundingTime;
            }
            Interlocked.Exchange(ref _fundingTimeCacheExpiryTicks, DateTime.UtcNow.AddSeconds(30).Ticks);

            if (_fundingTimeCache.TryGetValue(symbol, out var time))
            {
                return time;
            }

            return ComputeNextSettlement8h();
        }
        catch (OperationCanceledException) { throw; }
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

    public void Dispose()
    {
        _symbolInfoLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.UsdFuturesApi.Account.GetBalancesAsync(ct: token),
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
                async token => await _restClient.UsdFuturesApi.Account.GetBracketsAsync(symbol, ct: token), ct);

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
        catch (OperationCanceledException) { throw; }
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
        // Double-checked locking to prevent duplicate exchange-info fetches on cold start
        if (_symbolInfoLoaded)
        {
            return;
        }

        // Skip retries for 30s after a failed fetch
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _symbolInfoFailedUntilTicks))
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
                async token => await _restClient.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(token), ct);

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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _symbolInfoFailedUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
            _logger.LogWarning("Failed to fetch exchange info for symbol info lookup: {Error}", ex.Message);
        }
        finally
        {
            _symbolInfoLock.Release();
        }

        // Position mode validation — outside semaphore to avoid extending critical section
        if (_symbolInfoLoaded && Interlocked.CompareExchange(ref _positionModeChecked, 1, 0) == 0)
        {
            try
            {
                var sdkPipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
                var positionModeResult = await sdkPipeline.ExecuteAsync(
                    async token => await _restClient.UsdFuturesApi.Account.GetPositionModeAsync(ct: token), ct);
                if (positionModeResult.Success && positionModeResult.Data.IsHedgeMode)
                {
                    _logger.LogCritical("Binance account is in hedge mode. Switch to one-way mode for correct operation.");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to check position mode: {Error}", ex.Message);
            }
        }
    }

    public async Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
    {
        try
        {
            var symbol = asset + "USDT";
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: token), ct);

            if (!result.Success)
            {
                return null;
            }

            var pos = result.Data?.FirstOrDefault(p => p.Symbol == symbol &&
                ((side == Side.Long && p.Quantity > 0) || (side == Side.Short && p.Quantity < 0)));
            return pos != null;
        }
        catch (OperationCanceledException) { throw; }
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
                async token => await _restClient.UsdFuturesApi.Account.GetBracketsAsync(symbol, ct: token), ct);

            if (!result.Success || result.Data is null)
            {
                return null;
            }

            var symbolBracket = result.Data.FirstOrDefault();
            if (symbolBracket?.Brackets is null || symbolBracket.Brackets.Length == 0)
            {
                return null;
            }

            return symbolBracket.Brackets
                .Select(b => new LeverageTier(
                    (decimal)b.Floor,
                    (decimal)b.Cap,
                    b.InitialLeverage,
                    b.MaintenanceMarginRatio))
                .OrderBy(t => t.NotionalFloor)
                .ToArray();
        }
        catch (OperationCanceledException) { throw; }
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
                async token => await _restClient.UsdFuturesApi.Account.GetPositionInformationAsync(symbol, ct: token), ct);

            if (!result.Success)
            {
                return null;
            }

            var pos = result.Data?.FirstOrDefault(p => p.Symbol == symbol && p.Quantity != 0);
            if (pos is null)
            {
                return null;
            }

            var marginUsed = pos.IsolatedMargin;
            var wallet = pos.IsolatedWallet;

            return new MarginStateDto
            {
                MarginUsed = marginUsed,
                MarginAvailable = wallet > marginUsed ? wallet - marginUsed : 0m,
                LiquidationPrice = pos.LiquidationPrice,
                MarginUtilizationPct = wallet > 0 ? marginUsed / wallet : 0m
            };
        }
        catch (OperationCanceledException) { throw; }
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
