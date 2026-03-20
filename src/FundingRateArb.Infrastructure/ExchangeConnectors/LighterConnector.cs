using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.DTOs;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Polly.Registry;

namespace FundingRateArb.Infrastructure.ExchangeConnectors;

/// <summary>
/// Exchange connector for the Lighter (zkLighter) DEX.
/// Uses a custom HttpClient for REST API calls and the native lighter-signer
/// library (via P/Invoke) for cryptographic order signing.
/// </summary>
public class LighterConnector : IExchangeConnector, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LighterConnector> _logger;
    private readonly IConfiguration _configuration;
    private readonly LighterSigner _signer;
    // Signer initialisation is deferred until the first trading call
    // to avoid blocking startup when credentials are not configured.
    private bool _signerInitialized;
    private readonly object _signerLock = new();

    // Monotonically increasing order counter to avoid clientOrderIndex collisions.
    // Instance field (not static) so two instances cannot produce duplicate clientOrderIndex values.
    private long _orderCounter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Cache market metadata (orderBookDetails) for 5 minutes
    private Dictionary<string, LighterOrderBookDetail>? _marketCache;
    private DateTime _marketCacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    // Cache leverage per market to skip redundant TryUpdateLeverageAsync calls
    private readonly ConcurrentDictionary<int, int> _leverageCache = new();

    // In-flight refresh task — prevents thundering-herd when multiple callers see an expired cache.
    // Concurrent callers await the same Task instead of all issuing independent HTTP requests.
    private Task<Dictionary<string, LighterOrderBookDetail>>? _pendingMarketRefresh;

    // Cached config values populated in EnsureSignerReady (read once, used many times)
    private long _accountIndex;
    private string _apiKeyIndexStr = "2";

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
        _ = pipelineProvider; // Pipeline not used: HttpClient-level resilience handles retries (M-LC3)
        _signer = new LighterSigner(logger);
    }

    public string ExchangeName => "Lighter";

    // ── Funding Rates ──
    // Note: HttpClient has AddStandardResilienceHandler applied at registration (Program.cs).
    // No internal Polly pipeline is used here to avoid double-retry (M-LC3).

    public async Task<List<FundingRateDto>> GetFundingRatesAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Fetching funding rates from Lighter DEX");

        var ratesTask = _httpClient.GetAsync("funding-rates", ct);
        var statsTask = _httpClient.GetAsync("exchangeStats", ct);
        var assetsTask = _httpClient.GetAsync("assetDetails", ct);

        await Task.WhenAll(ratesTask, statsTask, assetsTask);

        var ratesHttpResponse = await ratesTask;
        ratesHttpResponse.EnsureSuccessStatusCode();
        var ratesResponse = await ratesHttpResponse.Content
            .ReadFromJsonAsync<LighterFundingRatesResponse>(JsonOptions, ct);

        LighterExchangeStatsResponse? statsResponse = null;
        try
        {
            var statsHttpResponse = await statsTask;
            statsHttpResponse.EnsureSuccessStatusCode();
            statsResponse = await statsHttpResponse.Content
                .ReadFromJsonAsync<LighterExchangeStatsResponse>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch exchange stats from Lighter; volume data will be unavailable");
        }

        var indexPriceBySymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var assetsHttpResponse = await assetsTask;
            assetsHttpResponse.EnsureSuccessStatusCode();
            var assetsResponse = await assetsHttpResponse.Content
                .ReadFromJsonAsync<LighterAssetDetailsResponse>(JsonOptions, ct);
            if (assetsResponse?.AssetDetails is not null)
            {
                foreach (var a in assetsResponse.AssetDetails)
                {
                    if (decimal.TryParse(a.IndexPrice, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var price) && price > 0)
                    {
                        indexPriceBySymbol[a.Symbol] = price;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch asset details from Lighter; mark prices will be unavailable");
        }

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
                MarkPrice    = indexPriceBySymbol.GetValueOrDefault(r.Symbol, 0m),
            }).ToList();
    }

    // ── Mark Price ──

    public async Task<decimal> GetMarkPriceAsync(string asset, CancellationToken ct = default)
    {
        _logger.LogDebug("Fetching mark price for {Asset} from Lighter DEX", asset);

        // Use orderBookDetails which includes last_trade_price (best available proxy for mark price)
        var market = await GetMarketDetailAsync(asset, ct);
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
    }

    // ── Available Balance ──

    public async Task<decimal> GetAvailableBalanceAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Fetching available balance from Lighter DEX");

        var accountIndex = GetAccountIndex();
        var accountResponse = await GetAccountAsync(accountIndex, ct);

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

    /// <remarks>
    /// IMPORTANT: Lighter's sendTx API returns only TxHash, not actual fill data.
    /// FilledPrice and FilledQuantity are estimates based on mark price at submission time.
    /// Actual fills may differ due to slippage. IsEstimatedFill is set to true.
    /// </remarks>
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

            // 2. Validate leverage against market's IMF limits
            if (market.MinInitialMarginFraction > 0)
            {
                var maxLeverage = 10_000 / market.MinInitialMarginFraction;
                if (leverage > maxLeverage)
                {
                    return new OrderResultDto
                    {
                        Success = false,
                        Error = $"Leverage {leverage}x exceeds {asset} market max {maxLeverage}x (MinIMF={market.MinInitialMarginFraction})"
                    };
                }
            }

            // 3. Update leverage for this market
            if (!_leverageCache.TryGetValue(market.MarketId, out var cachedLeverage) || cachedLeverage != leverage)
            {
                if (!await TryUpdateLeverageAsync(market.MarketId, leverage, ct))
                {
                    return new OrderResultDto
                    {
                        Success = false,
                        Error = $"Failed to set leverage {leverage}x for {asset} on Lighter"
                    };
                }
                _leverageCache[market.MarketId] = leverage;
            }

            // 4. Calculate base amount in Lighter integer format
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

            if (baseAmount > 1_000_000_000_000L)
                throw new InvalidOperationException($"Base amount {baseAmount} exceeds safety limit");

            // 5. Calculate price in Lighter integer format with slippage
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
            if (priceInt <= 0 || priceInt > int.MaxValue)
                throw new InvalidOperationException($"Price {priceInt} exceeds int range for Lighter API");

            // 6. Get nonce and sign order
            var nonce = await GetNextNonceAsync(ct);
            var clientOrderIndex = (int)(Interlocked.Increment(ref _orderCounter) % int.MaxValue);

            _logger.LogDebug(
                "Signing market order: market={MarketId} base={BaseAmount} price={Price} isAsk={IsAsk} nonce={Nonce}",
                market.MarketId, baseAmount, priceInt, isAsk, nonce);

            var (txType, txInfo, txHash) = _signer.SignMarketOrder(
                market.MarketId, clientOrderIndex, baseAmount, (int)priceInt,
                isAsk, reduceOnly: false, nonce);

            // 7. Submit the signed transaction
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
                IsEstimatedFill = true,
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

    /// <remarks>
    /// IMPORTANT: Lighter's sendTx API returns only TxHash, not actual fill data.
    /// FilledPrice and FilledQuantity are estimates based on mark price at submission time.
    /// Actual fills may differ due to slippage. IsEstimatedFill is set to true.
    /// </remarks>
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

            // 2. Get current position size from account (single HTTP fetch via helper)
            var accountIndex = GetAccountIndex();
            var accountResponse = await GetAccountAsync(accountIndex, ct);

            var account = accountResponse?.Accounts?.FirstOrDefault();
            if (account is null)
                throw new InvalidOperationException("Account not found on Lighter DEX");

            // Find the position and parse its size in one pass (avoids double TryParse)
            decimal positionSize = 0m;
            LighterAccountPosition? matchedPosition = null;
            foreach (var p in account.Positions ?? [])
            {
                if (!p.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    continue;
                var absSize = Math.Abs(parsed);
                if (absSize <= 0)
                    continue;
                positionSize = absSize;
                matchedPosition = p;
                break;
            }

            if (matchedPosition is null)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = $"No open position found for '{asset}' on Lighter DEX"
                };
            }
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

            if (baseAmount > 1_000_000_000_000L)
                throw new InvalidOperationException($"Base amount {baseAmount} exceeds safety limit");

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
            if (priceInt <= 0 || priceInt > int.MaxValue)
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
                IsEstimatedFill = true,
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
    /// Also populates cached config fields (_accountIndex, _apiKeyIndexStr) used by GetNextNonceAsync
    /// so that IConfiguration is not re-read on every order.
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

            var indexStr = _configuration["Exchanges:Lighter:AccountIndex"] ?? "281474976624240";
            if (!long.TryParse(indexStr, out var accountIndex))
                throw new InvalidOperationException($"Invalid Lighter AccountIndex: {indexStr}");

            // Cache for use in GetNextNonceAsync and GetAccountAsync (avoids re-reading IConfiguration)
            _accountIndex = accountIndex;
            _apiKeyIndexStr = apiKeyStr;

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
    /// Used for non-trading calls (e.g. GetAvailableBalanceAsync) that run before EnsureSignerReady.
    /// </summary>
    private long GetAccountIndex()
    {
        // If already cached by EnsureSignerReady, return cached value immediately
        if (_signerInitialized) return _accountIndex;

        var indexStr = _configuration["Exchanges:Lighter:AccountIndex"] ?? "281474976624240";
        if (!long.TryParse(indexStr, out var accountIndex))
            throw new InvalidOperationException($"Invalid Lighter AccountIndex: {indexStr}");
        return accountIndex;
    }

    /// <summary>
    /// Fetch the account data for the configured account index from the Lighter API.
    /// Extracted helper to avoid duplicate HTTP fetches in ClosePositionAsync.
    /// </summary>
    private async Task<LighterAccountResponse?> GetAccountAsync(long accountIndex, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(
            $"account?by=index&value={accountIndex}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<LighterAccountResponse>(JsonOptions, ct);
    }

    /// <summary>
    /// Fetch the next nonce from the Lighter API.
    /// Uses cached _accountIndex and _apiKeyIndexStr (populated by EnsureSignerReady)
    /// to avoid re-reading IConfiguration on every order.
    /// </summary>
    private async Task<int> GetNextNonceAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(
            $"nextNonce?account_index={_accountIndex}&api_key_index={_apiKeyIndexStr}", ct);
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
    private async Task<bool> TryUpdateLeverageAsync(int marketId, int leverage, CancellationToken ct)
    {
        _logger.LogDebug("Updating leverage for market {MarketId} to {Leverage}x", marketId, leverage);

        try
        {
            var nonce = await GetNextNonceAsync(ct);
            var (txType, txInfo, _) = _signer.SignLeverageUpdate(marketId, leverage, nonce);
            await SendTransactionAsync(txType, txInfo, ct);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Failed to set leverage for market {MarketId} to {Leverage}x: {Error}. Order will proceed with current leverage.",
                marketId, leverage, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Get market metadata for a specific asset symbol. Cached for 5 minutes.
    /// Prevents thundering-herd: concurrent callers on an expired cache all await the same in-flight
    /// HTTP fetch via <see cref="_pendingMarketRefresh"/> rather than each issuing a separate request.
    /// The lock is NOT held during HTTP I/O — it is acquired only to check/set _pendingMarketRefresh
    /// and to commit the new cache.
    /// </summary>
    private async Task<LighterOrderBookDetail?> GetMarketDetailAsync(string asset, CancellationToken ct)
    {
        // 1. Fast path: cache is warm — no locking needed
        var currentCache = _marketCache;
        if (currentCache is not null && DateTime.UtcNow < _marketCacheExpiry)
            return currentCache.GetValueOrDefault(asset);

        // 2. Cache miss — acquire lock to inspect/create a shared in-flight refresh task
        Task<Dictionary<string, LighterOrderBookDetail>> refreshTask;

        await _cacheLock.WaitAsync(ct);
        try
        {
            // Re-check while under lock (another thread may have just refreshed)
            if (_marketCache is not null && DateTime.UtcNow < _marketCacheExpiry)
            {
                return _marketCache.GetValueOrDefault(asset);
            }

            // Reuse an already in-flight refresh task if one exists (thundering-herd prevention)
            if (_pendingMarketRefresh is not null)
            {
                refreshTask = _pendingMarketRefresh;
            }
            else
            {
                // First caller: start the fetch and store it so subsequent callers can share it
                _pendingMarketRefresh = FetchMarketCacheAsync(ct);
                refreshTask = _pendingMarketRefresh;
            }
        }
        finally
        {
            _cacheLock.Release();
        }

        // 3. Await the shared fetch OUTSIDE the lock (no I/O held under lock)
        var newCache = await refreshTask;

        // 4. Commit the result and clear the pending task
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_marketCache is null || DateTime.UtcNow >= _marketCacheExpiry)
            {
                _marketCache = newCache;
                _marketCacheExpiry = DateTime.UtcNow + CacheTtl;
                _logger.LogDebug("Refreshed Lighter market cache: {Count} markets", _marketCache.Count);
            }
            // Clear the pending task so the next expiry triggers a fresh fetch
            _pendingMarketRefresh = null;
        }
        finally
        {
            _cacheLock.Release();
        }

        return _marketCache!.GetValueOrDefault(asset);
    }

    /// <summary>
    /// Performs the actual HTTP fetch for market metadata. Called by GetMarketDetailAsync
    /// as the shared in-flight task — only executed once per cache expiry cycle.
    /// </summary>
    private async Task<Dictionary<string, LighterOrderBookDetail>> FetchMarketCacheAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync("orderBookDetails?filter=perp", ct);
        response.EnsureSuccessStatusCode();

        var detailsResponse = await response.Content
            .ReadFromJsonAsync<LighterOrderBookDetailsResponse>(JsonOptions, ct);

        return detailsResponse?.OrderBookDetails?
            .ToDictionary(d => d.Symbol, d => d, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, LighterOrderBookDetail>(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _cacheLock.Dispose();
    }
}
