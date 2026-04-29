using System.Collections.Concurrent;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using FundingRateArb.Application.Common;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Binance Spot connector.
/// Spot has no funding rates, no leverage, and cannot take short positions.
/// GetFundingRatesAsync returns an empty list. MarketType is hardcoded to Spot.
/// PlaceMarketOrderByQuantityAsync logs a warning when leverage != 1 and proceeds (never throws).
/// </summary>
public class BinanceConnector : IExchangeConnector, IDisposable
{
    private readonly IBinanceRestClient _restClient;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly ILogger<BinanceConnector> _logger;
    private readonly IMarkPriceCache _markPriceCache;
    private readonly ConcurrentDictionary<string, int> _quantityPrecisionCache = new();
    private readonly ConcurrentDictionary<string, decimal> _tickSizeCache = new();
    private readonly SemaphoreSlim _symbolInfoLock = new(1, 1);
    private volatile bool _symbolInfoLoaded;
    private long _symbolInfoFailedUntilTicks = DateTime.MinValue.Ticks;

    public BinanceConnector(
        IBinanceRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<BinanceConnector> logger,
        IMarkPriceCache markPriceCache)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
        _logger = logger;
        _markPriceCache = markPriceCache;
    }

    public string ExchangeName => "Binance";

    public bool IsEstimatedFillExchange => false;

    /// <summary>
    /// Hardcoded to Spot — this connector speaks Binance Spot, not USD-M Futures.
    /// Never configure this at runtime via DB or appsettings (analysis Constraint).
    /// </summary>
    public ExchangeMarketType MarketType => ExchangeMarketType.Spot;

    /// <summary>
    /// Spot has no funding. Returns an empty list without making any API call.
    /// </summary>
    public Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
        => Task.FromResult(new List<FundingRateDto>());

    /// <inheritdoc />
    /// <remarks>
    /// Places a spot market order using quoteQuantity (USDC notional).
    /// Spot does not support shorting — returns failure for Side.Short.
    /// Ignores leverage (logs a warning when leverage != 1).
    /// </remarks>
    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        if (leverage != 1)
        {
            _logger.LogWarning(
                "Binance Spot ignoring leverage={Leverage} (spot has no leverage); proceeding with leverage=1",
                leverage);
        }

        if (side == Side.Short)
        {
            return new OrderResultDto { Success = false, Error = "Spot does not support shorting" };
        }

        var symbol = asset + "USDT";
        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Buy,
                SpotOrderType.Market,
                quoteQuantity: sizeUsdc,
                ct: token),
            ct);

        if (!result.Success)
        {
            return new OrderResultDto { Success = false, Error = result.Error?.Message ?? "Unknown error" };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success = true,
            OrderId = order.Id.ToString(),
            FilledPrice = order.AverageFillPrice ?? 0m,
            FilledQuantity = order.QuantityFilled,
        };
    }

    /// <summary>
    /// Places a spot market order by quantity.
    /// Spot does not support shorting — returns failure for Side.Short.
    /// Ignores leverage (logs a warning when leverage != 1).
    /// </summary>
    public async Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(
        string asset, Side side, decimal quantity, int leverage, string? clientOrderId = null, CancellationToken ct = default)
    {
        if (leverage != 1)
        {
            _logger.LogWarning(
                "Binance Spot ignoring leverage={Leverage} (spot has no leverage); proceeding with leverage=1",
                leverage);
        }

        if (side == Side.Short)
        {
            return new OrderResultDto { Success = false, Error = "Spot does not support shorting" };
        }

        var symbol = asset + "USDT";
        var qtyPrecision = await GetSymbolQuantityPrecisionAsync(symbol, ct);
        var roundedQuantity = Math.Round(quantity, qtyPrecision, MidpointRounding.ToZero);

        if (roundedQuantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Rounded quantity is zero for {asset} (quantity={quantity}, precision={qtyPrecision})" };
        }

        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Buy,
                SpotOrderType.Market,
                quantity: roundedQuantity,
                newClientOrderId: clientOrderId,
                ct: token),
            ct);

        if (!result.Success)
        {
            return new OrderResultDto { Success = false, Error = result.Error?.Message ?? "Unknown error" };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success = true,
            OrderId = order.Id.ToString(),
            FilledPrice = order.AverageFillPrice ?? 0m,
            FilledQuantity = order.QuantityFilled,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sells the held base-asset balance to close a long spot position.
    /// Spot cannot be short, so Side.Short always returns failure.
    /// </remarks>
    public async Task<OrderResultDto> ClosePositionAsync(
        string asset, Side side, CancellationToken ct = default)
    {
        if (side == Side.Short)
        {
            return new OrderResultDto { Success = false, Error = "Spot does not support short positions" };
        }

        var symbol = asset + "USDT";
        var balance = await GetSpotBaseAssetBalanceAsync(asset, ct);

        if (balance <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"No spot balance for {asset}" };
        }

        var qtyPrecision = await GetSymbolQuantityPrecisionAsync(symbol, ct);
        var quantity = Math.Round(balance, qtyPrecision, MidpointRounding.ToZero);

        if (quantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Rounded sell quantity is zero for {asset}" };
        }

        var pipeline = _pipelineProvider.GetPipeline("OrderClose");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.SpotApi.Trading.PlaceOrderAsync(
                symbol,
                OrderSide.Sell,
                SpotOrderType.Market,
                quantity: quantity,
                ct: token),
            ct);

        if (!result.Success)
        {
            return new OrderResultDto { Success = false, Error = result.Error?.Message ?? "Unknown error" };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success = true,
            OrderId = order.Id.ToString(),
            FilledPrice = order.AverageFillPrice ?? 0m,
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
                async t => await _restClient.SpotApi.ExchangeData.GetTickersAsync(ct: t),
                token);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error?.Message ?? "Unknown error");
            }

            var cache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var ticker in result.Data!)
            {
                if (ticker.LastPrice > 0)
                    cache[ticker.Symbol] = ticker.LastPrice;
            }

            return cache;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.SpotApi.Account.GetAccountInfoAsync(ct: token),
            ct);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error?.Message ?? "Unknown error");
        }

        return result.Data!.Balances
            .Where(b => b.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Available);
    }

    /// <summary>
    /// Spot has no leverage. Always returns 1.
    /// </summary>
    public Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
        => Task.FromResult<int?>(1);

    /// <summary>
    /// Spot has no funding settlement. Always returns null.
    /// </summary>
    public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
        => Task.FromResult<DateTime?>(null);

    /// <inheritdoc />
    public async Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
    {
        if (side == Side.Short)
            return false; // Spot cannot be short

        try
        {
            var balance = await GetSpotBaseAssetBalanceAsync(asset, ct);
            return balance > 0m;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Asset, Side Side, decimal Size)>?> GetAllOpenPositionsAsync(CancellationToken ct = default)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.SpotApi.Account.GetAccountInfoAsync(ct: token),
                ct);

            if (!result.Success || result.Data is null)
                return null;

            return result.Data.Balances
                .Where(b => !b.Asset.Equals("USDT", StringComparison.OrdinalIgnoreCase)
                            && b.Total > 0m)
                .Select(b => (b.Asset, Side.Long, b.Total))
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch Binance spot open positions");
            return null;
        }
    }

    /// <summary>
    /// Returns the number of decimal places used for order quantities on Binance Spot for the given asset.
    /// </summary>
    public async Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        return await GetSymbolQuantityPrecisionAsync(symbol, ct);
    }

    /// <summary>
    /// Spot has no leverage tiers. Always returns null.
    /// </summary>
    public Task<LeverageTier[]?> GetLeverageTiersAsync(string asset, CancellationToken ct = default)
        => Task.FromResult<LeverageTier[]?>(null);

    // IExchangeConnector defaults cover: GetRealizedPnlAsync, GetFundingPaymentsAsync,
    // GetPositionMarginStateAsync, GetCommissionIncomeAsync.

    public void Dispose()
    {
        _symbolInfoLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<decimal> GetSpotBaseAssetBalanceAsync(string asset, CancellationToken ct)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.SpotApi.Account.GetBalancesAsync(ct: token),
            ct);

        if (!result.Success || result.Data is null)
            return 0m;

        return result.Data
            .Where(b => b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Available);
    }

    private async Task<int> GetSymbolQuantityPrecisionAsync(string symbol, CancellationToken ct)
    {
        if (_quantityPrecisionCache.TryGetValue(symbol, out var cached))
            return cached;

        await EnsureSymbolInfoCachedAsync(ct);
        return _quantityPrecisionCache.GetValueOrDefault(symbol, 3);
    }

    private async Task EnsureSymbolInfoCachedAsync(CancellationToken ct)
    {
        if (_symbolInfoLoaded)
            return;

        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _symbolInfoFailedUntilTicks))
            return;

        await _symbolInfoLock.WaitAsync(ct);
        try
        {
            if (_symbolInfoLoaded)
                return;

            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(ct: token),
                ct);

            if (result.Success && result.Data?.Symbols != null)
            {
                foreach (var s in result.Data.Symbols)
                {
                    _quantityPrecisionCache[s.Name] = s.BaseAssetPrecision;

                    var priceFilter = s.PriceFilter;
                    if (priceFilter?.TickSize is > 0)
                        _tickSizeCache[s.Name] = priceFilter.TickSize;
                    else
                    {
                        decimal divisor = 1m;
                        for (int i = 0; i < s.QuoteAssetPrecision; i++)
                            divisor *= 10m;
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
            _logger.LogWarning("Failed to fetch Binance Spot exchange info: {Error}", ex.Message);
        }
        finally
        {
            _symbolInfoLock.Release();
        }
    }
}
