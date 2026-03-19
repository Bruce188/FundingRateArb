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

    public HyperliquidConnector(
        IHyperLiquidRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
    }

    public string ExchangeName => "Hyperliquid";

    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.ExchangeData.GetExchangeInfoAndTickersAsync(token),
            ct);

        if (!result.Success)
            throw new InvalidOperationException(result.Error!.ToString());

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
        // Fetch current mark price for market order slippage parameter
        var markPrice = await GetMarkPriceAsync(asset, ct);

        // Set leverage before placing the order
        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");
        await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.SetLeverageAsync(
                asset, leverage, MarginType.Cross, null, null, token),
            ct);

        var sdkSide = side == Side.Long ? OrderSide.Buy : OrderSide.Sell;
        var szDecimals = _szDecimalsCache.TryGetValue(asset, out var cached) ? cached : 6;
        var quantity = Math.Round(sizeUsdc * leverage / markPrice, szDecimals, MidpointRounding.ToZero);

        var orderResult = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol: asset,
                side: sdkSide,
                orderType: OrderType.Market,
                quantity: quantity,
                price: markPrice,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: false,
                clientOrderId: null,
                triggerPrice: null,
                tpSlType: null,
                tpSlGrouping: null,
                vaultAddress: null,
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

    public async Task<OrderResultDto> ClosePositionAsync(
        string asset,
        Side side,
        CancellationToken ct = default)
    {
        var markPrice = await GetMarkPriceAsync(asset, ct);

        // Try to get actual position size from account info
        var quantity = await GetPositionQuantityAsync(asset, ct);
        if (quantity <= 0)
        {
            // Fallback: use a large quantity. With reduceOnly=true the SDK treats it
            // as an upper bound and only closes the actual position size.
            quantity = decimal.MaxValue / 1_000_000m;
        }

        // To close: place opposite side with reduceOnly=true
        var closingSide = side == Side.Long ? OrderSide.Sell : OrderSide.Buy;

        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");
        var orderResult = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol: asset,
                side: closingSide,
                orderType: OrderType.Market,
                quantity: quantity,
                price: markPrice,
                timeInForce: TimeInForce.ImmediateOrCancel,
                reduceOnly: true,
                clientOrderId: null,
                triggerPrice: null,
                tpSlType: null,
                tpSlGrouping: null,
                vaultAddress: null,
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
                throw new InvalidOperationException(result.Error!.ToString());

            if (result.Data.ExchangeInfo?.Symbols != null)
            {
                foreach (var s in result.Data.ExchangeInfo.Symbols)
                    _szDecimalsCache[s.Name] = s.QuantityDecimals;
            }

            var cache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in result.Data.Tickers)
                cache[t.Symbol] = t.MarkPrice;
            return cache;
        }, ct);
    }

    public void Dispose()
    {
        _markPriceCache.Dispose();
    }

    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Account.GetAccountInfoAsync(null, null, token),
            ct);

        if (!result.Success)
            throw new InvalidOperationException(result.Error!.ToString());

        return result.Data.Withdrawable;
    }

    /// <summary>
    /// Fetches the actual open position quantity for the given asset from account info.
    /// Returns 0 if no position is found or the call fails.
    /// </summary>
    private async Task<decimal> GetPositionQuantityAsync(string asset, CancellationToken ct)
    {
        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async token => await _restClient.FuturesApi.Account.GetAccountInfoAsync(null, null, token),
                ct);

            if (!result.Success || result.Data.Positions is null)
                return 0m;

            var position = result.Data.Positions
                .FirstOrDefault(p => p.Position?.Symbol == asset);

            var qty = position?.Position?.PositionQuantity ?? 0m;
            return qty > 0 ? Math.Abs(qty) : 0m;
        }
        catch
        {
            // If we cannot fetch positions, return 0 to trigger the fallback
            return 0m;
        }
    }
}
