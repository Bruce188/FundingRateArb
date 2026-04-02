using System.Collections.Concurrent;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using HyperLiquid.Net.Enums;
using HyperLiquid.Net.Interfaces.Clients;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

public class HyperliquidConnector : IExchangeConnector, IDisposable
{
    private readonly IHyperLiquidRestClient _restClient;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly MarkPriceCacheHelper _markPriceCache = new();
    private readonly ConcurrentDictionary<string, int> _szDecimalsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _vaultAddress;

    private const decimal SlippagePct = 0.005m; // 0.5% max slippage

    public HyperliquidConnector(
        IHyperLiquidRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider,
        string? vaultAddress = null)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
        _vaultAddress = vaultAddress;
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
                return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below Hyperliquid minimum $10.00" };
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

    public async Task<OrderResultDto> ClosePositionAsync(
        string asset,
        Side side,
        CancellationToken ct = default)
    {
        var markPrice = await GetMarkPriceAsync(asset, ct);

        // Try to get actual position size from account info
        var (quantity, apiConfirmed) = await GetPositionQuantityAsync(asset, ct);
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
        return await _markPriceCache.GetOrRefreshAsync(asset, async token =>
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

    public void Dispose()
    {
        _markPriceCache.Dispose();
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
    /// Fetches the actual open position quantity for the given asset from account info.
    /// Returns 0 if no position is found or the call fails.
    /// </summary>
    /// <summary>
    /// Returns (quantity, apiConfirmed). apiConfirmed=true means the API successfully
    /// returned data (position genuinely doesn't exist). apiConfirmed=false means the
    /// API call failed or returned an unexpected result (transient — allow retry).
    /// </summary>
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
            // API succeeded — if qty is 0, the position genuinely doesn't exist
            return qty > 0 ? (Math.Abs(qty), true) : (0m, true);
        }
        catch
        {
            // Exception during API call — transient, allow retry
            return (0m, false);
        }
    }
}
