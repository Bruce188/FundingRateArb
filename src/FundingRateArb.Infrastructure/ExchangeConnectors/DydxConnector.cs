using System.Net.Http.Json;
using System.Text.Json;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Domain.ValueObjects;
using FundingRateArb.Infrastructure.ExchangeConnectors.Dydx;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// dYdX v4 exchange connector. Uses Indexer REST API for reads and Cosmos tx signing
/// (via <see cref="DydxSigner"/>) for order placement. Rates are already per-hour;
/// no normalization is needed. Funding is continuous hourly.
/// </summary>
public sealed class DydxConnector : IExchangeConnector, IDisposable
{
    private readonly HttpClient _indexerClient;
    private readonly HttpClient _validatorClient;
    private readonly DydxSigner? _signer;
    private readonly ResiliencePipelineProvider<string> _pipelineProvider;
    private readonly ILogger<DydxConnector> _logger;
    private readonly IMarkPriceCache _markPriceCache;

    // Market info cache (double-checked locking, same pattern as BinanceConnector)
    private readonly SemaphoreSlim _marketInfoLock = new(1, 1);
    private volatile bool _marketInfoLoaded;
    private long _marketInfoFailedUntilTicks = DateTime.MinValue.Ticks;
    private long _marketInfoExpiryTicks = DateTime.MinValue.Ticks;
    private Dictionary<string, DydxPerpetualMarket> _marketCache = new(StringComparer.OrdinalIgnoreCase);

    // Block height cache (short TTL for goodTilBlock)
    private long _cachedBlockHeight;
    private long _blockHeightExpiryTicks;

    // Local sequence counter for rapid successive orders
    private ulong _cachedAccountNumber;
    private long _cachedSequence;
    private volatile bool _accountInfoCached;

    /// <summary>Default slippage tolerance for market orders (5%).</summary>
    private const decimal SlippagePct = 0.05m;

    /// <summary>dYdX chain ID for mainnet.</summary>
    private const string ChainId = "dydx-mainnet-1";

    /// <summary>Short-term IOC order flag.</summary>
    private const uint OrderFlagShortTerm = 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DydxConnector(
        HttpClient indexerClient,
        HttpClient validatorClient,
        DydxSigner? signer,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<DydxConnector> logger,
        IMarkPriceCache markPriceCache)
    {
        _indexerClient = indexerClient ?? throw new ArgumentNullException(nameof(indexerClient));
        _validatorClient = validatorClient ?? throw new ArgumentNullException(nameof(validatorClient));
        _signer = signer; // nullable — read-only operations work without signing
        _pipelineProvider = pipelineProvider;
        _logger = logger;
        _markPriceCache = markPriceCache;
    }

    public string ExchangeName => "dYdX";

    public bool IsEstimatedFillExchange => false;

    private DydxSigner RequireSigner() =>
        _signer ?? throw new InvalidOperationException(
            "dYdX connector requires signing credentials. Configure a mnemonic via user exchange credentials.");

    /// <inheritdoc />
    /// <remarks>
    /// Calls <c>GET /perpetualMarkets</c> from the Indexer. dYdX rates are already per-hour;
    /// no normalization is needed. Symbol format is "BTC-USD" — the "-USD" suffix is stripped
    /// for the normalized symbol.
    /// </remarks>
    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var markets = await pipeline.ExecuteAsync(async token =>
        {
            var resp = await _indexerClient.GetFromJsonAsync<DydxPerpetualMarketsResponse>(
                "perpetualMarkets", JsonOptions, token);
            return resp?.Markets ?? new Dictionary<string, DydxPerpetualMarket>();
        }, ct);

        var result = new List<FundingRateDto>();
        foreach (var (_, market) in markets)
        {
            if (!market.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rate = market.NextFundingRate ?? 0m;
            var symbol = market.Ticker.EndsWith("-USD")
                ? market.Ticker[..^4]
                : market.Ticker;

            result.Add(new FundingRateDto
            {
                ExchangeName = ExchangeName,
                Symbol = symbol,
                RawRate = rate,
                RatePerHour = rate, // dYdX rates are already per-hour
                MarkPrice = market.OraclePrice,
                IndexPrice = market.OraclePrice,
                Volume24hUsd = 0m, // Indexer does not include 24h volume in perpetualMarkets
            });
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        var ticker = asset + "-USD";

        var markPrice = await GetMarkPriceAsync(asset, ct);
        if (markPrice <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
        }

        var marketInfo = await GetMarketInfoAsync(ticker, ct);
        if (marketInfo is null)
        {
            return new OrderResultDto { Success = false, Error = $"Market info not found for {ticker}" };
        }

        var quantity = Math.Round(
            sizeUsdc * leverage / markPrice,
            GetDecimalPlaces(marketInfo.StepSize),
            MidpointRounding.ToZero);

        if (quantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Calculated quantity is zero for {asset} (size={sizeUsdc}, leverage={leverage}, mark={markPrice})" };
        }

        var notional = quantity * markPrice;
        if (notional < 1m)
        {
            return new OrderResultDto { Success = false, Error = $"Order notional ${notional:F2} below minimum" };
        }

        return await PlaceOrderInternalAsync(asset, side, quantity, markPrice, marketInfo, ct);
    }

    /// <inheritdoc />
    public async Task<OrderResultDto> PlaceMarketOrderByQuantityAsync(
        string asset, Side side, decimal quantity, int leverage, CancellationToken ct = default)
    {
        var ticker = asset + "-USD";

        var markPrice = await GetMarkPriceAsync(asset, ct);
        if (markPrice <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Mark price is zero or negative for {asset}" };
        }

        var marketInfo = await GetMarketInfoAsync(ticker, ct);
        if (marketInfo is null)
        {
            return new OrderResultDto { Success = false, Error = $"Market info not found for {ticker}" };
        }

        var roundedQuantity = Math.Round(
            quantity,
            GetDecimalPlaces(marketInfo.StepSize),
            MidpointRounding.ToZero);

        if (roundedQuantity <= 0)
        {
            return new OrderResultDto { Success = false, Error = $"Rounded quantity is zero for {asset} (quantity={quantity})" };
        }

        return await PlaceOrderInternalAsync(asset, side, roundedQuantity, markPrice, marketInfo, ct);
    }

    /// <inheritdoc />
    public async Task<int> GetQuantityPrecisionAsync(string asset, CancellationToken ct = default)
    {
        var ticker = asset + "-USD";
        var marketInfo = await GetMarketInfoAsync(ticker, ct);
        return marketInfo is not null ? GetDecimalPlaces(marketInfo.StepSize) : 3;
    }

    /// <inheritdoc />
    public async Task<OrderResultDto> ClosePositionAsync(
        string asset, Side side, CancellationToken ct = default)
    {
        var ticker = asset + "-USD";

        var marketInfo = await GetMarketInfoAsync(ticker, ct);
        if (marketInfo is null)
        {
            return new OrderResultDto { Success = false, Error = $"Market info not found for {ticker}" };
        }

        // Fetch position from Indexer
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        DydxPositionsResponse? positionsResp;
        try
        {
            positionsResp = await pipeline.ExecuteAsync(async token =>
                await _indexerClient.GetFromJsonAsync<DydxPositionsResponse>(
                    $"addresses/{RequireSigner().Address}/subaccountNumber/0/perpetualPositions?status=OPEN",
                    JsonOptions, token), ct);
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, Error = $"Failed to fetch positions: {ex.Message}" };
        }

        var expectedSide = side == Side.Long ? "LONG" : "SHORT";
        var position = positionsResp?.Positions?.FirstOrDefault(p =>
            p.Market.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            p.Side.Equals(expectedSide, StringComparison.OrdinalIgnoreCase));

        if (position is null)
        {
            return new OrderResultDto { Success = false, Error = $"No open {side} position for {ticker}" };
        }

        var quantity = Math.Abs(position.Size);
        var markPrice = await GetMarkPriceAsync(asset, ct);

        // Build reduce-only order on opposite side
        var closeSide = side == Side.Long ? DydxOrderSide.Sell : DydxOrderSide.Buy;
        var limitPrice = closeSide == DydxOrderSide.Buy
            ? RoundToTickSize(markPrice * (1m + SlippagePct), marketInfo.TickSize)
            : RoundToTickSize(markPrice * (1m - SlippagePct), marketInfo.TickSize);

        var quantums = ToQuantums(quantity, marketInfo.AtomicResolution);
        var subticks = ToSubticks(limitPrice, marketInfo.AtomicResolution, marketInfo.QuantumConversionExponent);
        var blockHeight = await GetCurrentBlockHeightAsync(ct);

        var (accountNumber, sequence) = await GetAccountSequenceAsync(ct);
        var clientId = (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);

        var order = new DydxOrder
        {
            OrderId = new DydxOrderId
            {
                SubaccountId = new DydxSubaccountId { Owner = RequireSigner().Address, SubaccountNumber = 0 },
                ClientId = clientId,
                OrderFlags = OrderFlagShortTerm,
                ClobPairId = (uint)marketInfo.ClobPairId,
            },
            Side = closeSide,
            Quantums = quantums,
            Subticks = subticks,
            GoodTilBlock = blockHeight + 20,
            TimeInForce = DydxTimeInForce.Ioc,
            ReduceOnly = true,
        };

        try
        {
            var orderPipeline = _pipelineProvider.GetPipeline("OrderClose");
            var txHash = await orderPipeline.ExecuteAsync(async token =>
            {
                var txBytes = RequireSigner().BuildAndSignPlaceOrderTx(order, accountNumber, sequence, ChainId);
                return await RequireSigner().BroadcastTxAsync(_validatorClient, txBytes, token);
            }, ct);

            IncrementSequence();

            return new OrderResultDto
            {
                Success = true,
                OrderId = txHash,
                FilledPrice = markPrice,
                FilledQuantity = quantity,
            };
        }
        catch (Exception ex)
        {
            _accountInfoCached = false;
            return new OrderResultDto { Success = false, Error = ex.Message };
        }
    }

    /// <inheritdoc />
    public async Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        var ticker = asset + "-USD";
        return await _markPriceCache.GetOrRefreshAsync(ExchangeName, ticker, async token =>
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var resp = await pipeline.ExecuteAsync(async t =>
                await _indexerClient.GetFromJsonAsync<DydxPerpetualMarketsResponse>(
                    "perpetualMarkets", JsonOptions, t), token);

            var cache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (resp?.Markets is not null)
            {
                foreach (var (key, market) in resp.Markets)
                {
                    cache[key] = market.OraclePrice;
                }
            }
            return cache;
        }, ct);
    }

    /// <inheritdoc />
    /// <remarks>dYdX funding is continuous hourly — returns the next hour boundary.</remarks>
    public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        return Task.FromResult<DateTime?>(next);
    }

    /// <inheritdoc />
    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
        var resp = await pipeline.ExecuteAsync(async token =>
            await _indexerClient.GetFromJsonAsync<DydxSubaccountResponse>(
                $"addresses/{RequireSigner().Address}/subaccountNumber/0", JsonOptions, token), ct);

        return resp?.Subaccount?.FreeCollateral ?? 0m;
    }

    /// <inheritdoc />
    public async Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
    {
        var ticker = asset + "-USD";
        var marketInfo = await GetMarketInfoAsync(ticker, ct);
        if (marketInfo is null || marketInfo.InitialMarginFraction <= 0)
        {
            return null;
        }

        return (int)(1m / marketInfo.InitialMarginFraction);
    }

    public async Task<LeverageTier[]?> GetLeverageTiersAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var ticker = asset + "-USD";
            var marketInfo = await GetMarketInfoAsync(ticker, ct);
            if (marketInfo is null || marketInfo.InitialMarginFraction <= 0)
                return null;

            var maxLeverage = (int)(1m / marketInfo.InitialMarginFraction);
            var maintMarginRate = marketInfo.InitialMarginFraction / 2m;

            return new[]
            {
                new LeverageTier(0m, decimal.MaxValue, maxLeverage, maintMarginRate)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to fetch leverage tiers for {Asset}: {Error}", asset, ex.Message);
            return null;
        }
    }

    public Task<MarginStateDto?> GetPositionMarginStateAsync(string asset, CancellationToken ct = default)
    {
        // dYdX indexer does not expose per-position margin/liquidation fields
        return Task.FromResult<MarginStateDto?>(null);
    }

    /// <inheritdoc />
    public async Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
    {
        try
        {
            var ticker = asset + "-USD";
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var resp = await pipeline.ExecuteAsync(async token =>
                await _indexerClient.GetFromJsonAsync<DydxPositionsResponse>(
                    $"addresses/{RequireSigner().Address}/subaccountNumber/0/perpetualPositions?status=OPEN",
                    JsonOptions, token), ct);

            var expectedSide = side == Side.Long ? "LONG" : "SHORT";
            var pos = resp?.Positions?.FirstOrDefault(p =>
                p.Market.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                p.Side.Equals(expectedSide, StringComparison.OrdinalIgnoreCase));

            return pos is not null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _signer?.Dispose();
        _marketInfoLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<OrderResultDto> PlaceOrderInternalAsync(
        string asset, Side side, decimal quantity, decimal markPrice,
        DydxPerpetualMarket marketInfo, CancellationToken ct)
    {
        var orderSide = side == Side.Long ? DydxOrderSide.Buy : DydxOrderSide.Sell;
        var limitPrice = orderSide == DydxOrderSide.Buy
            ? RoundToTickSize(markPrice * (1m + SlippagePct), marketInfo.TickSize)
            : RoundToTickSize(markPrice * (1m - SlippagePct), marketInfo.TickSize);

        var quantums = ToQuantums(quantity, marketInfo.AtomicResolution);
        var subticks = ToSubticks(limitPrice, marketInfo.AtomicResolution, marketInfo.QuantumConversionExponent);
        var blockHeight = await GetCurrentBlockHeightAsync(ct);

        var (accountNumber, sequence) = await GetAccountSequenceAsync(ct);
        var clientId = (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);

        var order = new DydxOrder
        {
            OrderId = new DydxOrderId
            {
                SubaccountId = new DydxSubaccountId { Owner = RequireSigner().Address, SubaccountNumber = 0 },
                ClientId = clientId,
                OrderFlags = OrderFlagShortTerm,
                ClobPairId = (uint)marketInfo.ClobPairId,
            },
            Side = orderSide,
            Quantums = quantums,
            Subticks = subticks,
            GoodTilBlock = blockHeight + 20,
            TimeInForce = DydxTimeInForce.Ioc,
            ReduceOnly = false,
        };

        try
        {
            var pipeline = _pipelineProvider.GetPipeline("OrderExecution");
            var txHash = await pipeline.ExecuteAsync(async token =>
            {
                var txBytes = RequireSigner().BuildAndSignPlaceOrderTx(order, accountNumber, sequence, ChainId);
                return await RequireSigner().BroadcastTxAsync(_validatorClient, txBytes, token);
            }, ct);

            IncrementSequence();

            return new OrderResultDto
            {
                Success = true,
                OrderId = txHash,
                FilledPrice = markPrice,
                FilledQuantity = quantity,
            };
        }
        catch (Exception ex)
        {
            _accountInfoCached = false;
            return new OrderResultDto { Success = false, Error = ex.Message };
        }
    }

    private async Task<DydxPerpetualMarket?> GetMarketInfoAsync(string ticker, CancellationToken ct)
    {
        await EnsureMarketInfoCachedAsync(ct);
        return _marketCache.GetValueOrDefault(ticker);
    }

    private async Task EnsureMarketInfoCachedAsync(CancellationToken ct)
    {
        // Reset cache after 1-hour TTL
        if (_marketInfoLoaded && DateTime.UtcNow.Ticks > Interlocked.Read(ref _marketInfoExpiryTicks))
        {
            _marketInfoLoaded = false;
        }

        if (_marketInfoLoaded)
        {
            return;
        }

        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _marketInfoFailedUntilTicks))
        {
            return;
        }

        await _marketInfoLock.WaitAsync(ct);
        try
        {
            if (_marketInfoLoaded)
            {
                return;
            }

            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var resp = await pipeline.ExecuteAsync(async token =>
                await _indexerClient.GetFromJsonAsync<DydxPerpetualMarketsResponse>(
                    "perpetualMarkets", JsonOptions, token), ct);

            if (resp?.Markets is not null && resp.Markets.Count > 0)
            {
                _marketCache = new Dictionary<string, DydxPerpetualMarket>(
                    resp.Markets, StringComparer.OrdinalIgnoreCase);
                Interlocked.Exchange(ref _marketInfoExpiryTicks, DateTime.UtcNow.AddHours(1).Ticks);
                _marketInfoLoaded = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _marketInfoFailedUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
            _logger.LogWarning("Failed to fetch dYdX market info: {Error}", ex.Message);
        }
        finally
        {
            _marketInfoLock.Release();
        }
    }

    private async Task<uint> GetCurrentBlockHeightAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _blockHeightExpiryTicks))
        {
            return (uint)Interlocked.Read(ref _cachedBlockHeight);
        }

        try
        {
            var pipeline = _pipelineProvider.GetPipeline("ExchangeSdk");
            var resp = await pipeline.ExecuteAsync(async token =>
                await _indexerClient.GetFromJsonAsync<DydxHeightResponse>(
                    "height", JsonOptions, token), ct);

            if (resp is not null)
            {
                Interlocked.Exchange(ref _cachedBlockHeight, resp.Height);
                Interlocked.Exchange(ref _blockHeightExpiryTicks, DateTime.UtcNow.AddSeconds(5).Ticks);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch dYdX block height: {Error}", ex.Message);
        }

        return (uint)Interlocked.Read(ref _cachedBlockHeight);
    }

    private async Task<(ulong AccountNumber, ulong Sequence)> GetAccountSequenceAsync(CancellationToken ct)
    {
        if (_accountInfoCached)
        {
            return (_cachedAccountNumber, (ulong)Interlocked.Read(ref _cachedSequence));
        }

        var (accountNumber, sequence) = await RequireSigner().GetAccountInfoAsync(_validatorClient, ct);
        _cachedAccountNumber = accountNumber;
        Interlocked.Exchange(ref _cachedSequence, (long)sequence);
        _accountInfoCached = true;
        return (accountNumber, sequence);
    }

    private void IncrementSequence()
    {
        if (_accountInfoCached)
        {
            Interlocked.Increment(ref _cachedSequence);
        }
    }

    internal static ulong ToQuantums(decimal quantity, int atomicResolution)
        => (ulong)(quantity * (decimal)Math.Pow(10, -atomicResolution));

    internal static ulong ToSubticks(decimal price, int atomicResolution, int quantumConversionExponent)
        => (ulong)(price * (decimal)Math.Pow(10, atomicResolution - quantumConversionExponent));

    private static decimal RoundToTickSize(decimal price, decimal tickSize)
    {
        if (tickSize <= 0)
        {
            return Math.Round(price, 2, MidpointRounding.AwayFromZero);
        }

        return Math.Round(price / tickSize, 0, MidpointRounding.AwayFromZero) * tickSize;
    }

    private static int GetDecimalPlaces(decimal stepSize)
    {
        if (stepSize <= 0)
        {
            return 0;
        }

        int places = 0;
        while (stepSize < 1m && places < 18)
        {
            stepSize *= 10m;
            places++;
        }
        return places;
    }
}
