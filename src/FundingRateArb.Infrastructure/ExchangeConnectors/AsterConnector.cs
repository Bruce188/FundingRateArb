using Aster.Net.Enums;
using Aster.Net.Interfaces.Clients;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Aster DEX connector. Aster publishes 4-hour funding rates; all rates are
/// normalised to per-hour before being returned (<see cref="FundingRateDto.RatePerHour"/> = rawRate / 4).
/// </summary>
public class AsterConnector : IExchangeConnector, IDisposable
{
    private readonly IAsterRestClient _restClient;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly MarkPriceCacheHelper _markPriceCache = new();

    public AsterConnector(
        IAsterRestClient restClient,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        _restClient = restClient;
        _pipelineProvider = pipelineProvider;
    }

    public string ExchangeName => "Aster";

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>UsdFuturesApi.ExchangeData.GetMarkPricesAsync</c> which returns the current
    /// mark price, index price and 4-hour funding rate for every symbol.
    /// Aster uses 4-hour funding intervals — rates are divided by 4 to normalise to per-hour.
    /// The original (undivided) value is preserved in <see cref="FundingRateDto.RawRate"/>.
    /// Symbol names are normalised: "ETHUSDT" → "ETH".
    /// </remarks>
    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var markPricesTask = pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.ExchangeData.GetMarkPricesAsync(token), ct);
        var tickersTask = pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.ExchangeData.GetTickersAsync(token), ct);

        await Task.WhenAll(markPricesTask.AsTask(), tickersTask.AsTask());

        var markPrices = await markPricesTask;
        var tickers    = await tickersTask;

        if (!markPrices.Success)
            throw new InvalidOperationException(markPrices.Error!.ToString());

        var volumeBySymbol = tickers.Success && tickers.Data is not null
            ? tickers.Data.ToDictionary(t => t.Symbol, t => t.QuoteVolume)
            : new Dictionary<string, decimal>();

        return markPrices.Data!
            .Select(mp => new FundingRateDto
            {
                ExchangeName = ExchangeName,
                Symbol       = mp.Symbol.EndsWith("USDT") ? mp.Symbol[..^4] : mp.Symbol,
                RawRate      = mp.FundingRate ?? 0m,
                RatePerHour  = (mp.FundingRate ?? 0m) / 4m,
                MarkPrice    = mp.MarkPrice,
                IndexPrice   = mp.IndexPrice,
                Volume24hUsd = volumeBySymbol.GetValueOrDefault(mp.Symbol, 0m),
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
        var quantity = sizeUsdc * leverage / markPrice;

        // Set leverage before placing the order (best-effort; ignore result)
        await _restClient.FuturesApi.Account.SetLeverageAsync(symbol, leverage, null, ct);

        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol,
                orderSide,
                OrderType.Market,
                quantity: quantity,
                price: null,
                positionSide: null,
                timeInForce: null,
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
                Error   = result.Error!.ToString(),
            };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success         = true,
            OrderId         = order.Id.ToString(),
            FilledPrice     = order.AveragePrice,
            FilledQuantity  = order.QuantityFilled,
        };
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

        var pipeline = _pipelineProvider.GetPipeline("OrderExecution");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Trading.PlaceOrderAsync(
                symbol,
                closeSide,
                OrderType.Market,
                quantity: null,
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
                Error   = result.Error!.ToString(),
            };
        }

        var order = result.Data!;
        return new OrderResultDto
        {
            Success        = true,
            OrderId        = order.Id.ToString(),
            FilledPrice    = order.AveragePrice,
            FilledQuantity = order.QuantityFilled,
        };
    }

    /// <inheritdoc />
    public async Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        var symbol = asset + "USDT";
        return await _markPriceCache.GetOrRefreshAsync(symbol, async token =>
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var result = await pipeline.ExecuteAsync(
                async t => await _restClient.FuturesApi.ExchangeData.GetMarkPricesAsync(t),
                token);
            if (!result.Success)
                throw new InvalidOperationException(result.Error!.ToString());

            var cache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var mp in result.Data!)
                cache[mp.Symbol] = mp.MarkPrice;
            return cache;
        }, ct);
    }

    public void Dispose()
    {
        _markPriceCache.Dispose();
    }

    /// <inheritdoc />
    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");

        var result = await pipeline.ExecuteAsync(
            async token => await _restClient.FuturesApi.Account.GetBalancesAsync(null, token),
            ct);

        if (!result.Success)
            throw new InvalidOperationException(result.Error!.ToString());

        return result.Data!.Sum(b => b.AvailableBalance);
    }
}
