using System.Net.Http.Json;
using System.Text.Json;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Exchange connector for the Lighter (zkLighter) DEX.
/// Uses a custom HttpClient for REST API calls and the native lighter-signer
/// library (via P/Invoke) for cryptographic order signing.
/// </summary>
public class LighterConnector : IExchangeConnector
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LighterConnector> _logger;
    private readonly IConfiguration _configuration;
    private readonly LighterSigner _signer;
    private readonly ResiliencePipelineProvider<string>? _pipelineProvider;

    // Signer initialisation is deferred until the first trading call
    // to avoid blocking startup when credentials are not configured.
    private bool _signerInitialized;
    private readonly object _signerLock = new();

    // Monotonically increasing order counter to avoid clientOrderIndex collisions
    private static long _orderCounter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Cache market metadata (orderBookDetails) for 5 minutes
    private Dictionary<string, LighterOrderBookDetail>? _marketCache;
    private DateTime _marketCacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Slippage tolerance for market orders (0.5%).</summary>
    private const decimal SlippagePct = 0.005m;

    public LighterConnector(
        HttpClient httpClient,
        ILogger<LighterConnector> logger,
        IConfiguration configuration,
        ResiliencePipelineProvider<string>? pipelineProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _pipelineProvider = pipelineProvider;
        _signer = new LighterSigner(logger);
    }

    public string ExchangeName => "Lighter";

    // ── Funding Rates (unchanged) ──

    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider?.GetPipeline("ExchangeSdk");

        return await ExecuteWithOptionalPipeline(pipeline, async token =>
        {
            _logger.LogDebug("Fetching funding rates from Lighter DEX");

            var ratesTask = _httpClient.GetAsync("funding-rates", token);
            var statsTask = _httpClient.GetAsync("exchangeStats", token);

            await Task.WhenAll(ratesTask, statsTask);

            var ratesResponse = await (await ratesTask).Content
                .ReadFromJsonAsync<LighterFundingRatesResponse>(JsonOptions, token);
            var statsResponse = await (await statsTask).Content
                .ReadFromJsonAsync<LighterExchangeStatsResponse>(JsonOptions, token);

            var allRates = ratesResponse?.FundingRates;
            if (allRates is null || allRates.Count == 0)
            {
                _logger.LogDebug("Lighter returned no funding rates");
                return [];
            }

            var volumeBySymbol = statsResponse?.OrderBookStats?
                .ToDictionary(s => s.Symbol, s => s.DailyQuoteTokenVolume)
                ?? new Dictionary<string, decimal>();

            // The endpoint returns rates for all reference exchanges; keep only Lighter's own rates
            return allRates
                .Where(r => r.Exchange.Equals("lighter", StringComparison.OrdinalIgnoreCase))
                .Select(r => new FundingRateDto
                {
                    ExchangeName = ExchangeName,
                    Symbol       = r.Symbol,
                    RawRate      = r.Rate,
                    RatePerHour  = r.Rate,
                    Volume24hUsd = volumeBySymbol.GetValueOrDefault(r.Symbol, 0m),
                }).ToList();
        }, ct);
    }

    // ── Mark Price ──

    public async Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        var pipeline = _pipelineProvider?.GetPipeline("ExchangeSdk");

        return await ExecuteWithOptionalPipeline(pipeline, async token =>
        {
            _logger.LogDebug("Fetching mark price for {Asset} from Lighter DEX", asset);

            // Use orderBookDetails which includes last_trade_price (best available proxy for mark price)
            var market = await GetMarketDetailAsync(asset, token);
            if (market is null)
            {
                throw new KeyNotFoundException(
                    $"Asset '{asset}' not found on Lighter DEX");
            }

            if (market.LastTradePrice <= 0)
            {
                _logger.LogWarning("Lighter last_trade_price is 0 for {Asset}, trying funding-rates endpoint", asset);
                throw new InvalidOperationException(
                    $"No valid price available for '{asset}' on Lighter DEX");
            }

            return market.LastTradePrice;
        }, ct);
    }

    // ── Available Balance ──

    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Fetching available balance from Lighter DEX");

        var accountIndex = GetAccountIndex();
        var response = await _httpClient.GetAsync(
            $"account?by=index&value={accountIndex}", ct);
        response.EnsureSuccessStatusCode();

        var accountResponse = await response.Content
            .ReadFromJsonAsync<LighterAccountResponse>(JsonOptions, ct);

        var account = accountResponse?.Accounts?.FirstOrDefault();
        if (account is null)
        {
            _logger.LogWarning("Account {AccountIndex} not found on Lighter DEX", accountIndex);
            return 0m;
        }

        if (decimal.TryParse(account.AvailableBalance, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var balance))
        {
            _logger.LogDebug("Lighter available balance: {Balance} USDC", balance);
            return balance;
        }

        _logger.LogWarning("Could not parse Lighter available balance: {Raw}", account.AvailableBalance);
        return 0m;
    }

    // ── Place Market Order ──

    public async Task<OrderResultDto> PlaceMarketOrderAsync(
        string asset, Side side, decimal sizeUsdc, int leverage, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "PlaceMarketOrderAsync: {Asset} {Side} sizeUsdc={SizeUsdc} leverage={Leverage}",
            asset, side, sizeUsdc, leverage);

        try
        {
            EnsureSignerReady();

            // 1. Get market metadata for the asset
            var market = await GetMarketDetailAsync(asset, ct)
                ?? throw new KeyNotFoundException($"Asset '{asset}' not found on Lighter DEX");

            var markPrice = market.LastTradePrice;
            if (markPrice <= 0)
                throw new InvalidOperationException($"No valid price for '{asset}' on Lighter");

            // 2. Update leverage for this market
            await UpdateLeverageAsync(market.MarketId, leverage, ct);

            // 3. Calculate base amount in Lighter integer format
            //    notional = sizeUsdc * leverage
            //    baseReal = notional / markPrice
            //    baseAmount = (long)(baseReal * 10^sizeDecimals)
            var notional = sizeUsdc * leverage;
            var baseReal = notional / markPrice;
            var sizeMultiplier = (long)Math.Pow(10, market.SizeDecimals);
            var baseAmount = (long)(baseReal * sizeMultiplier);

            if (baseAmount <= 0)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = $"Calculated base amount is zero (notional={notional}, price={markPrice})"
                };
            }

            // 4. Calculate price in Lighter integer format with slippage
            var priceMultiplier = (long)Math.Pow(10, market.PriceDecimals);
            bool isAsk = side == Side.Short;
            long priceInt;
            if (isAsk)
            {
                // Selling: accept slightly lower price
                priceInt = (long)(markPrice * (1m - SlippagePct) * priceMultiplier);
            }
            else
            {
                // Buying: accept slightly higher price
                priceInt = (long)(markPrice * (1m + SlippagePct) * priceMultiplier);
            }

            // Bounds check: native signer's SignCreateOrder takes int price
            if (priceInt > int.MaxValue || priceInt < int.MinValue)
                throw new InvalidOperationException($"Price {priceInt} exceeds int range for Lighter API");

            // 5. Get nonce and sign order
            var nonce = await GetNextNonceAsync(ct);
            var clientOrderIndex = (int)(Interlocked.Increment(ref _orderCounter) % int.MaxValue);

            _logger.LogDebug(
                "Signing market order: market={MarketId} base={BaseAmount} price={Price} isAsk={IsAsk} nonce={Nonce}",
                market.MarketId, baseAmount, priceInt, isAsk, nonce);

            var (txType, txInfo, txHash) = _signer.SignMarketOrder(
                market.MarketId, clientOrderIndex, baseAmount, (int)priceInt,
                isAsk, reduceOnly: false, nonce);

            // 6. Submit the signed transaction
            var sendResult = await SendTransactionAsync(txType, txInfo, ct);

            _logger.LogInformation(
                "Order placed on Lighter: txHash={TxHash} market={Asset} side={Side}",
                sendResult.TxHash ?? txHash, asset, side);

            return new OrderResultDto
            {
                Success = true,
                OrderId = sendResult.TxHash ?? txHash,
                FilledPrice = markPrice,
                FilledQuantity = baseReal,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaceMarketOrderAsync failed for {Asset}", asset);
            return new OrderResultDto
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    // ── Close Position ──

    public async Task<OrderResultDto> ClosePositionAsync(
        string asset, Side side, CancellationToken ct = default)
    {
        _logger.LogInformation("ClosePositionAsync: {Asset} {Side}", asset, side);

        try
        {
            EnsureSignerReady();

            // 1. Get market metadata
            var market = await GetMarketDetailAsync(asset, ct)
                ?? throw new KeyNotFoundException($"Asset '{asset}' not found on Lighter DEX");

            var markPrice = market.LastTradePrice;
            if (markPrice <= 0)
                throw new InvalidOperationException($"No valid price for '{asset}' on Lighter");

            // 2. Get current position size from account
            var accountIndex = GetAccountIndex();
            var response = await _httpClient.GetAsync(
                $"account?by=index&value={accountIndex}", ct);
            response.EnsureSuccessStatusCode();

            var accountResponse = await response.Content
                .ReadFromJsonAsync<LighterAccountResponse>(JsonOptions, ct);

            var account = accountResponse?.Accounts?.FirstOrDefault();
            if (account is null)
                throw new InvalidOperationException("Account not found on Lighter DEX");

            var position = account.Positions?.FirstOrDefault(
                p => p.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase)
                     && decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                         System.Globalization.CultureInfo.InvariantCulture, out var posSize)
                     && Math.Abs(posSize) > 0);

            if (position is null)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = $"No open position found for '{asset}' on Lighter DEX"
                };
            }

            // Parse position size (already in base asset decimal form)
            if (!decimal.TryParse(position.Position, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var positionSize))
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = $"Could not parse position size: {position.Position}"
                };
            }

            positionSize = Math.Abs(positionSize);
            var sizeMultiplier = (long)Math.Pow(10, market.SizeDecimals);
            var baseAmount = (long)(positionSize * sizeMultiplier);

            if (baseAmount <= 0)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = $"Position size is zero for '{asset}'"
                };
            }

            // 3. To close: reverse the side (Long->Sell, Short->Buy)
            bool isAsk = side == Side.Long;

            // 4. Calculate price with slippage
            var priceMultiplier = (long)Math.Pow(10, market.PriceDecimals);
            long priceInt;
            if (isAsk)
            {
                priceInt = (long)(markPrice * (1m - SlippagePct) * priceMultiplier);
            }
            else
            {
                priceInt = (long)(markPrice * (1m + SlippagePct) * priceMultiplier);
            }

            // Bounds check: native signer's SignCreateOrder takes int price
            if (priceInt > int.MaxValue || priceInt < int.MinValue)
                throw new InvalidOperationException($"Price {priceInt} exceeds int range for Lighter API");

            // 5. Sign and submit
            var nonce = await GetNextNonceAsync(ct);
            var clientOrderIndex = (int)(Interlocked.Increment(ref _orderCounter) % int.MaxValue);

            _logger.LogDebug(
                "Signing close order: market={MarketId} base={BaseAmount} price={Price} isAsk={IsAsk} reduceOnly=true",
                market.MarketId, baseAmount, priceInt, isAsk);

            var (txType, txInfo, txHash) = _signer.SignMarketOrder(
                market.MarketId, clientOrderIndex, baseAmount, (int)priceInt,
                isAsk, reduceOnly: true, nonce);

            var sendResult = await SendTransactionAsync(txType, txInfo, ct);

            _logger.LogInformation(
                "Position closed on Lighter: txHash={TxHash} market={Asset}",
                sendResult.TxHash ?? txHash, asset);

            return new OrderResultDto
            {
                Success = true,
                OrderId = sendResult.TxHash ?? txHash,
                FilledPrice = markPrice,
                FilledQuantity = positionSize,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClosePositionAsync failed for {Asset}", asset);
            return new OrderResultDto
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    // ── Private helpers ──

    /// <summary>
    /// Ensure the native signer is initialized with credentials from user secrets.
    /// Thread-safe; only initializes once.
    /// </summary>
    private void EnsureSignerReady()
    {
        if (_signerInitialized) return;
        lock (_signerLock)
        {
            if (_signerInitialized) return;

            var privateKey = _configuration["Exchanges:Lighter:SignerPrivateKey"]
                ?? throw new InvalidOperationException(
                    "Lighter SignerPrivateKey not found in configuration. " +
                    "Set it via: dotnet user-secrets set \"Exchanges:Lighter:SignerPrivateKey\" \"0x...\"");

            var apiKeyStr = _configuration["Exchanges:Lighter:ApiKey"] ?? "2";
            if (!int.TryParse(apiKeyStr, out var apiKeyIndex))
                throw new InvalidOperationException($"Invalid Lighter ApiKey value: {apiKeyStr}");

            var accountIndex = GetAccountIndex();

            // Base URL without /api/v1/ suffix (the signer needs the root URL)
            var baseUrl = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "";
            if (baseUrl.EndsWith("/api/v1"))
                baseUrl = baseUrl[..^"/api/v1".Length];

            _signer.Initialize(baseUrl, privateKey, apiKeyIndex, accountIndex);
            _signerInitialized = true;
        }
    }

    /// <summary>
    /// Get the account index from configuration.
    /// </summary>
    private long GetAccountIndex()
    {
        var indexStr = _configuration["Exchanges:Lighter:AccountIndex"] ?? "281474976624240";
        if (!long.TryParse(indexStr, out var accountIndex))
            throw new InvalidOperationException($"Invalid Lighter AccountIndex: {indexStr}");
        return accountIndex;
    }

    /// <summary>
    /// Fetch the next nonce from the Lighter API.
    /// </summary>
    private async Task<int> GetNextNonceAsync(CancellationToken ct)
    {
        var accountIndex = GetAccountIndex();
        var apiKeyStr = _configuration["Exchanges:Lighter:ApiKey"] ?? "2";

        var response = await _httpClient.GetAsync(
            $"nextNonce?account_index={accountIndex}&api_key_index={apiKeyStr}", ct);
        response.EnsureSuccessStatusCode();

        var nonceResponse = await response.Content
            .ReadFromJsonAsync<LighterNonceResponse>(JsonOptions, ct);

        return nonceResponse?.Nonce ?? throw new InvalidOperationException("Failed to fetch nonce from Lighter");
    }

    /// <summary>
    /// Submit a signed transaction to the Lighter API via POST /api/v1/sendTx.
    /// </summary>
    private async Task<LighterSendTxResponse> SendTransactionAsync(
        byte txType, string txInfo, CancellationToken ct)
    {
        // The Lighter API expects multipart/form-data with tx_type and tx_info fields
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(txType.ToString()), "tx_type");
        form.Add(new StringContent(txInfo), "tx_info");

        var response = await _httpClient.PostAsync("sendTx", form, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lighter sendTx failed: {StatusCode} {Body}",
                response.StatusCode, responseBody);
            throw new HttpRequestException(
                $"Lighter order submission failed ({response.StatusCode})");
        }

        var result = JsonSerializer.Deserialize<LighterSendTxResponse>(responseBody, JsonOptions);
        if (result is null)
            throw new InvalidOperationException("Failed to deserialize Lighter sendTx response");

        if (result.Code != 200)
        {
            throw new InvalidOperationException(
                $"Lighter sendTx returned error code {result.Code}: {result.Message}");
        }

        _logger.LogDebug("Lighter sendTx success: txHash={TxHash}", result.TxHash);
        return result;
    }

    /// <summary>
    /// Update leverage for a market via signed transaction.
    /// </summary>
    private async Task UpdateLeverageAsync(int marketId, int leverage, CancellationToken ct)
    {
        _logger.LogDebug("Updating leverage for market {MarketId} to {Leverage}x", marketId, leverage);

        var nonce = await GetNextNonceAsync(ct);
        var (txType, txInfo, _) = _signer.SignLeverageUpdate(marketId, leverage, nonce);
        await SendTransactionAsync(txType, txInfo, ct);
    }

    /// <summary>
    /// Executes an async function with an optional Polly resilience pipeline.
    /// If no pipeline provider was injected, the function runs directly.
    /// </summary>
    private static async Task<T> ExecuteWithOptionalPipeline<T>(
        Polly.ResiliencePipeline? pipeline,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken ct)
    {
        if (pipeline is not null)
            return await pipeline.ExecuteAsync(action, ct);

        return await action(ct);
    }

    /// <summary>
    /// Get market metadata for a specific asset symbol. Cached for 5 minutes.
    /// </summary>
    private async Task<LighterOrderBookDetail?> GetMarketDetailAsync(string asset, CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_marketCache is null || DateTime.UtcNow >= _marketCacheExpiry)
            {
                var response = await _httpClient.GetAsync("orderBookDetails?filter=perp", ct);
                response.EnsureSuccessStatusCode();

                var detailsResponse = await response.Content
                    .ReadFromJsonAsync<LighterOrderBookDetailsResponse>(JsonOptions, ct);

                _marketCache = detailsResponse?.OrderBookDetails?
                    .ToDictionary(d => d.Symbol, d => d, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, LighterOrderBookDetail>(StringComparer.OrdinalIgnoreCase);
                _marketCacheExpiry = DateTime.UtcNow + CacheTtl;

                _logger.LogDebug("Refreshed Lighter market cache: {Count} markets", _marketCache.Count);
            }

            return _marketCache.GetValueOrDefault(asset);
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
