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
public class LighterConnector : IExchangeConnector, IPositionVerifiable, IDisposable
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

    // Cache market metadata (orderBookDetails) for 45 seconds
    private Dictionary<string, LighterOrderBookDetail>? _marketCache;
    private DateTime _marketCacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    // Cache leverage per market to skip redundant TryUpdateLeverageAsync calls
    private readonly ConcurrentDictionary<int, int> _leverageCache = new();

    // Last predicted settlement time from SendTransactionAsync, used for smarter verification delays
    private double _lastPredictedSettlementMs;

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

    /// <summary>Default slippage tolerance for market orders (0.5%).</summary>
    private const decimal DefaultSlippagePct = 0.005m;

    /// <summary>Maximum slippage tolerance (2%).</summary>
    private const decimal MaxSlippagePct = 0.02m;

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

    public bool IsEstimatedFillExchange => true;

    /// <inheritdoc />
    /// <remarks>
    /// Waits for the zk-rollup transaction to settle using a dynamic initial delay
    /// (5-15s, clamped from the predicted execution time returned by sendTx, default 8s),
    /// then polls GetAccountAsync up to 10 times with linearly increasing backoff (2-6s).
    /// If all polls are exhausted, performs a final 10s grace check.
    /// Detects the target position by comparing current sizes against a baseline snapshot
    /// taken before polling, rather than relying on total position count.
    /// </remarks>
    public async Task<bool> VerifyPositionOpenedAsync(string asset, Side side, CancellationToken ct = default)
    {
        const int maxAttempts = 10;

        var accountIndex = GetAccountIndex();

        // Record baseline position sizes before polling starts (tuple key for safety)
        var baseline = new Dictionary<(string Symbol, string Side), decimal>();
        try
        {
            var baselineResponse = await GetAccountAsync(accountIndex, ct);
            var baselineAccount = baselineResponse?.Accounts?.FirstOrDefault();
            if (baselineAccount?.Positions is not null)
            {
                foreach (var p in baselineAccount.Positions)
                {
                    if (decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var sz) && sz != 0)
                    {
                        var sideKey = sz > 0 ? "Long" : "Short";
                        baseline[(p.Symbol.ToUpperInvariant(), sideKey)] = Math.Abs(sz);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture baseline positions for {Asset} verification — proceeding without baseline", asset);
        }

        // Allow time for the zk-rollup transaction to settle before polling
        var predictedMs = Volatile.Read(ref _lastPredictedSettlementMs);
        Volatile.Write(ref _lastPredictedSettlementMs, 0);
        var initialDelayMs = ComputeInitialDelayMs(predictedMs);
        await Task.Delay(initialDelayMs, ct);

        // Baseline-aware early-exit — if the target asset+side is absent for 5
        // consecutive polls with no size delta observed, treat as a non-fill.
        int noChangeStreak = 0;
        const int noChangeEarlyExit = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Delay between retries, not before the first attempt (linearly increasing backoff)
            if (attempt > 0)
            {
                var delayMs = Math.Min(6000, 2000 + attempt * 1000);
                await Task.Delay(delayMs, ct);
            }

            try
            {
                var accountResponse = await GetAccountAsync(accountIndex, ct);
                var account = accountResponse?.Accounts?.FirstOrDefault();

                // Filter zero-size positions (closed but still listed)
                var nonZeroPositions = account?.Positions?.Where(p =>
                    decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var sz) && sz != 0).ToList();
                var posCount = nonZeroPositions?.Count ?? 0;

                // Log first and last poll attempts at Information level for production visibility
                var pollLogLevel = (attempt == 0 || attempt == maxAttempts - 1)
                    ? LogLevel.Information : LogLevel.Debug;

                if (nonZeroPositions is null || nonZeroPositions.Count == 0)
                {
                    _logger.Log(pollLogLevel,
                        "Verify poll {Attempt}/{Max}: asset={Asset} side={Side} found=false positionCount=0",
                        attempt + 1, maxAttempts, asset, side);

                    noChangeStreak++;
                    if (noChangeStreak >= noChangeEarlyExit)
                    {
                        _logger.LogInformation(
                            "Verify: no size change for {N} consecutive polls after baseline — treating as non-fill for {Asset} {Side}",
                            noChangeStreak, asset, side);
                        return false;
                    }
                    continue;
                }

                var matchResult = TryMatchPosition(nonZeroPositions, asset, side, baseline);

                if (matchResult.IsNewOrIncreased)
                {
                    _logger.Log(pollLogLevel,
                        "Verify poll {Attempt}/{Max}: asset={Asset} side={Side} found=true positionCount={Count} size={Size} baselineSize={Baseline}",
                        attempt + 1, maxAttempts, asset, side, posCount, matchResult.Size,
                        matchResult.BaselineSize);
                    return true;
                }

                _logger.Log(pollLogLevel,
                    "Verify poll {Attempt}/{Max}: asset={Asset} side={Side} found=false positionCount={Count}",
                    attempt + 1, maxAttempts, asset, side, posCount);

                if (!matchResult.FoundAtBaseline)
                {
                    noChangeStreak++;
                }
                else
                {
                    noChangeStreak = 0; // target visible at baseline size — order may still be settling
                }

                if (noChangeStreak >= noChangeEarlyExit)
                {
                    _logger.LogInformation(
                        "Verify: no size change for {N} consecutive polls after baseline — treating as non-fill for {Asset} {Side}",
                        noChangeStreak, asset, side);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Position verification attempt {Attempt}/{Max} failed for {Asset}",
                    attempt + 1, maxAttempts, asset);
            }
        }

        // Final grace check — one more attempt after extended delay
        _logger.LogInformation(
            "Verify: all {Max} polls exhausted for {Asset} {Side} — waiting 10s for final grace check",
            maxAttempts, asset, side);
        await Task.Delay(10_000, ct);
        try
        {
            var graceResponse = await GetAccountAsync(accountIndex, ct);
            var graceAccount = graceResponse?.Accounts?.FirstOrDefault();
            if (graceAccount?.Positions is not null)
            {
                var graceResult = TryMatchPosition(graceAccount.Positions, asset, side, baseline);
                if (graceResult.IsNewOrIncreased)
                {
                    _logger.LogInformation(
                        "Verify GRACE CHECK: found position for {Asset} {Side} size={Size}",
                        asset, side, graceResult.Size);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Grace check failed for {Asset}", asset);
        }

        _logger.LogWarning(
            "Position verification FAILED after {Max} polls + grace check: asset={Asset} side={Side} accountIndex={AccountIndex}",
            maxAttempts, asset, side, accountIndex);
        return false;
    }

    /// <summary>
    /// Computes adaptive slippage based on the bid-ask spread.
    /// Floor: 0.5% (DefaultSlippagePct). Cap: 2% (MaxSlippagePct).
    /// For wide spreads (>= 0.2%): max(0.5%, spread * 2) capped at 2%.
    /// Falls back to 0.5% if bid or ask is zero/invalid.
    /// </summary>
    internal static decimal ComputeSlippagePct(decimal bestBid, decimal bestAsk)
    {
        if (bestBid <= 0 || bestAsk <= 0 || bestBid >= bestAsk)
        {
            return DefaultSlippagePct;
        }

        var spread = (bestAsk - bestBid) / bestBid;

        // Tight spread (< 0.2%) → use floor
        if (spread < 0.002m)
        {
            return DefaultSlippagePct;
        }

        // Wide spread → max(floor, spread * 2) capped at MaxSlippagePct
        var adaptive = Math.Max(DefaultSlippagePct, spread * 2m);
        return Math.Min(adaptive, MaxSlippagePct);
    }

    /// <summary>
    /// Gets the per-market slippage percentage from cached market data.
    /// Falls back to DefaultSlippagePct if bid/ask data is unavailable.
    /// </summary>
    private decimal GetSlippagePct(LighterOrderBookDetail market)
    {
        var slippage = ComputeSlippagePct(market.BestBid, market.BestAsk);
        if (slippage != DefaultSlippagePct)
        {
            _logger.LogDebug(
                "Adaptive slippage for {Symbol}: {Slippage}% (bid={Bid} ask={Ask})",
                market.Symbol, slippage * 100, market.BestBid, market.BestAsk);
        }
        return slippage;
    }

    /// <summary>
    /// Computes the initial delay before polling begins, clamped to [5000, 15000] ms.
    /// Returns 8000 ms when the predicted time is zero, negative, or non-finite.
    /// </summary>
    internal static int ComputeInitialDelayMs(double predictedMs)
    {
        if (predictedMs > 0 && double.IsFinite(predictedMs))
        {
            return (int)Math.Clamp(predictedMs, 5000, 15000);
        }

        return 8000;
    }

    /// <summary>
    /// Checks whether any position in the list matches the target asset and side,
    /// and whether its size is new or increased compared to the baseline snapshot.
    /// </summary>
    private static PositionMatchResult TryMatchPosition(
        IEnumerable<LighterAccountPosition> positions,
        string asset,
        Side side,
        Dictionary<(string Symbol, string Side), decimal> baseline)
    {
        foreach (var p in positions)
        {
            if (!p.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) || size == 0)
            {
                continue;
            }

            var isSideMatch = (side == Side.Long && size > 0) || (side == Side.Short && size < 0);
            if (!isSideMatch)
            {
                continue;
            }

            var absSize = Math.Abs(size);
            var sideStr = size > 0 ? "Long" : "Short";
            var baselineKey = (asset.ToUpperInvariant(), sideStr);

            if (!baseline.TryGetValue(baselineKey, out var baselineSize) || absSize > baselineSize)
            {
                return new PositionMatchResult(true, false, size, baseline.ContainsKey(baselineKey) ? baselineSize : 0m);
            }

            // Position exists at baseline size — not new or increased
            return new PositionMatchResult(false, true, size, baselineSize);
        }

        return new PositionMatchResult(false, false, 0m, 0m);
    }

    private readonly record struct PositionMatchResult(
        bool IsNewOrIncreased,
        bool FoundAtBaseline,
        decimal Size,
        decimal BaselineSize);

    /// <summary>
    /// Checks whether a position exists on this exchange for the given asset and side.
    /// Delegates to CheckPositionExistsAsync.
    /// </summary>
    public Task<bool?> HasOpenPositionAsync(string asset, Side side, CancellationToken ct = default)
        => CheckPositionExistsAsync(asset, side, baseline: null, ct);

    /// <summary>
    /// Captures a snapshot of current position sizes for baseline comparison.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string Symbol, string Side), decimal>?> CapturePositionSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var accountIndex = GetAccountIndex();
            var accountResponse = await GetAccountAsync(accountIndex, cts.Token);
            var account = accountResponse?.Accounts?.FirstOrDefault();

            var snapshot = new Dictionary<(string Symbol, string Side), decimal>();
            if (account?.Positions is null)
            {
                return snapshot;
            }

            foreach (var p in account.Positions)
            {
                if (decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var sz) && sz != 0)
                {
                    var sideKey = sz > 0 ? "Long" : "Short";
                    snapshot[(p.Symbol.ToUpperInvariant(), sideKey)] = Math.Abs(sz);
                }
            }

            return snapshot;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture position snapshot for baseline");
            return null;
        }
    }

    /// <summary>
    /// Single read-only check whether a position exists for the given asset and side.
    /// When baseline is provided, returns true only if position size increased vs baseline.
    /// </summary>
    public async Task<bool?> CheckPositionExistsAsync(string asset, Side side,
        IReadOnlyDictionary<(string Symbol, string Side), decimal>? baseline = null,
        CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var accountIndex = GetAccountIndex();
            var accountResponse = await GetAccountAsync(accountIndex, cts.Token);
            var account = accountResponse?.Accounts?.FirstOrDefault();

            if (account is null)
            {
                _logger.LogWarning("Account not found on Lighter when checking position existence for {Asset}", asset);
                return null;
            }

            if (account.Positions is null)
            {
                return false;
            }

            foreach (var p in account.Positions)
            {
                if (!p.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var size) || size == 0)
                {
                    continue;
                }

                var isSideMatch = (side == Side.Long && size > 0) || (side == Side.Short && size < 0);
                if (isSideMatch)
                {
                    if (baseline is null)
                    {
                        return true;
                    }

                    var sideKey = size > 0 ? "Long" : "Short";
                    var key = (p.Symbol.ToUpperInvariant(), sideKey);
                    var currentSize = Math.Abs(size);
                    var baselineSize = baseline.TryGetValue(key, out var bs) ? bs : 0m;

                    if (currentSize > baselineSize)
                    {
                        return true;
                    }

                    _logger.LogWarning(
                        "Position exists for {Asset} {Side} but size {Current} <= baseline {Baseline} — treating as pre-existing",
                        asset, side, currentSize, baselineSize);
                    return false;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check position existence for {Asset} {Side}", asset, side);
            return null;
        }
    }

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

        // Fall back to orderBookDetails (LastTradePrice) for symbols missing from assetDetails
        var lighterSymbols = allRates
            .Where(r => r.Exchange.Equals("lighter", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missingPriceSymbols = lighterSymbols
            .Where(s => !indexPriceBySymbol.TryGetValue(s, out var p) || p <= 0)
            .ToList();

        if (missingPriceSymbols.Count > 0)
        {
            _logger.LogDebug("Lighter assetDetails missing mark prices for {Count} symbols, trying orderBookDetails fallback", missingPriceSymbols.Count);
            foreach (var symbol in missingPriceSymbols)
            {
                try
                {
                    var market = await GetMarketDetailAsync(symbol, ct);
                    if (market is not null && market.LastTradePrice > 0)
                    {
                        indexPriceBySymbol[symbol] = market.LastTradePrice;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Mark price fallback failed for {Symbol}", symbol);
                }
            }
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
                Symbol = r.Symbol,
                RawRate = r.Rate,
                RatePerHour = r.Rate / 8m,
                Volume24hUsd = volumeBySymbol.GetValueOrDefault(r.Symbol, 0m),
                MarkPrice = indexPriceBySymbol.GetValueOrDefault(r.Symbol, 0m),
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

            _logger.LogDebug(
                "Market detail: {Asset} marketId={MarketId} price={Price} sizeDecimals={SizeDecimals}",
                asset, market.MarketId, markPrice, market.SizeDecimals);

            if (markPrice <= 0)
            {
                throw new InvalidOperationException($"No valid price for '{asset}' on Lighter");
            }

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

            // Min notional validation — use MinBaseAmount if available, else $5 USDC fallback
            var actualNotional = baseReal * markPrice;
            var minBaseAmountParsed = long.TryParse(market.MinBaseAmount, out var minBase) ? minBase : 0L;
            var minNotional = minBaseAmountParsed > 0
                ? (decimal)minBaseAmountParsed / sizeMultiplier * markPrice
                : 5m;
            if (actualNotional < minNotional)
            {
                return new OrderResultDto
                {
                    Success = false,
                    Error = $"Order notional ${actualNotional:F2} below Lighter minimum ${minNotional:F2}"
                };
            }

            if (baseAmount > 1_000_000_000_000L)
            {
                throw new InvalidOperationException($"Base amount {baseAmount} exceeds safety limit");
            }

            // 5. Calculate price in Lighter integer format with adaptive slippage
            var slippagePct = GetSlippagePct(market);
            var priceMultiplier = (long)Math.Pow(10, market.PriceDecimals);
            bool isAsk = side == Side.Short;
            long priceInt;
            if (isAsk)
            {
                // Selling: accept slightly lower price
                priceInt = (long)(markPrice * (1m - slippagePct) * priceMultiplier);
            }
            else
            {
                // Buying: accept slightly higher price
                priceInt = (long)(markPrice * (1m + slippagePct) * priceMultiplier);
            }

            // Bounds check: native signer's SignCreateOrder takes int price
            if (priceInt <= 0 || priceInt > int.MaxValue)
            {
                throw new InvalidOperationException($"Price {priceInt} exceeds int range for Lighter API");
            }

            // 6. Get nonce and sign order
            var nonce = await GetNextNonceAsync(ct);
            var clientOrderIndex = (int)(Interlocked.Increment(ref _orderCounter) % int.MaxValue);

            _logger.LogDebug(
                "Nonce for order: {Nonce} accountIndex={AccountIndex}",
                nonce, _accountIndex);

            var (txType, txInfo, txHash) = _signer.SignMarketOrder(
                market.MarketId, clientOrderIndex, baseAmount, (int)priceInt,
                isAsk, reduceOnly: false, nonce);

            _logger.LogDebug(
                "Signed order: txHash={TxHash} baseAmount={BaseAmount} price={PriceInt} isAsk={IsAsk}",
                txHash, baseAmount, priceInt, isAsk);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Lighter order params: {Asset} {Side} baseAmount={BaseAmount} priceInt={PriceInt} slippage={Slippage}% markPrice={MarkPrice} notional={Notional}",
                    asset, side, baseAmount, priceInt, slippagePct * 100, markPrice, notional);
            }

            // 7. Submit the signed transaction
            var sendResult = await SendTransactionAsync(txType, txInfo, ct);
            Volatile.Write(ref _lastPredictedSettlementMs, sendResult.PredictedExecutionTimeMs);

            var effectiveTxHash = sendResult.TxHash ?? txHash;

            _logger.LogInformation(
                "Order placed on Lighter: txHash={TxHash} market={Asset} side={Side}",
                effectiveTxHash, asset, side);

            // 8. Check tx status — early-exit if the order failed on-chain
            if (!string.IsNullOrEmpty(effectiveTxHash))
            {
                var (proceed, txStatus) = await CheckTxStatusAsync(effectiveTxHash, ct);
                if (!proceed)
                {
                    // TX failed — look up cancellation reason for diagnostics
                    var reason = await GetCancellationReasonAsync(market.MarketId, ct);
                    var errorMsg = reason is not null
                        ? $"Order failed on-chain: {reason} (txHash={effectiveTxHash})"
                        : $"Order failed on-chain (txHash={effectiveTxHash})";

                    _logger.LogWarning(
                        "Order failed on Lighter: txHash={TxHash} reason={Reason}",
                        effectiveTxHash, reason ?? "unknown");

                    return new OrderResultDto
                    {
                        Success = false,
                        OrderId = effectiveTxHash,
                        Error = errorMsg
                    };
                }
            }

            return new OrderResultDto
            {
                Success = true,
                OrderId = effectiveTxHash,
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
            {
                throw new InvalidOperationException($"No valid price for '{asset}' on Lighter");
            }

            // 2. Get current position size from account (single HTTP fetch via helper)
            var accountIndex = GetAccountIndex();
            var accountResponse = await GetAccountAsync(accountIndex, ct);

            var account = accountResponse?.Accounts?.FirstOrDefault();
            if (account is null)
            {
                throw new InvalidOperationException("Account not found on Lighter DEX");
            }

            // Find the position and parse its size in one pass (avoids double TryParse)
            decimal positionSize = 0m;
            LighterAccountPosition? matchedPosition = null;
            foreach (var p in account.Positions ?? [])
            {
                if (!p.Symbol.Equals(asset, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!decimal.TryParse(p.Position, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                {
                    continue;
                }

                var absSize = Math.Abs(parsed);
                if (absSize <= 0)
                {
                    continue;
                }

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
            {
                throw new InvalidOperationException($"Base amount {baseAmount} exceeds safety limit");
            }

            // 3. To close: reverse the side (Long->Sell, Short->Buy)
            bool isAsk = side == Side.Long;

            // 4. Calculate price with adaptive slippage
            var slippagePct = GetSlippagePct(market);
            var priceMultiplier = (long)Math.Pow(10, market.PriceDecimals);
            long priceInt;
            if (isAsk)
            {
                priceInt = (long)(markPrice * (1m - slippagePct) * priceMultiplier);
            }
            else
            {
                priceInt = (long)(markPrice * (1m + slippagePct) * priceMultiplier);
            }

            // Bounds check: native signer's SignCreateOrder takes int price
            if (priceInt <= 0 || priceInt > int.MaxValue)
            {
                throw new InvalidOperationException($"Price {priceInt} exceeds int range for Lighter API");
            }

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
        if (_signerInitialized)
        {
            return;
        }

        lock (_signerLock)
        {
            if (_signerInitialized)
            {
                return;
            }

            var privateKey = _configuration["Exchanges:Lighter:SignerPrivateKey"]
                ?? throw new InvalidOperationException(
                    "Lighter SignerPrivateKey not found in configuration. " +
                    "Set it via: dotnet user-secrets set \"Exchanges:Lighter:SignerPrivateKey\" \"0x...\"");

            var apiKeyStr = _configuration["Exchanges:Lighter:ApiKey"] ?? "2";
            if (!int.TryParse(apiKeyStr, out var apiKeyIndex))
            {
                throw new InvalidOperationException($"Invalid Lighter ApiKey value: {apiKeyStr}");
            }

            var indexStr = _configuration["Exchanges:Lighter:AccountIndex"] ?? "281474976624240";
            if (!long.TryParse(indexStr, out var accountIndex))
            {
                throw new InvalidOperationException($"Invalid Lighter AccountIndex: {indexStr}");
            }

            // Cache for use in GetNextNonceAsync and GetAccountAsync (avoids re-reading IConfiguration)
            _accountIndex = accountIndex;
            _apiKeyIndexStr = apiKeyStr;

            // Base URL without /api/v1/ suffix (the signer needs the root URL)
            var baseUrl = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "";
            if (baseUrl.EndsWith("/api/v1"))
            {
                baseUrl = baseUrl[..^"/api/v1".Length];
            }

            _signer.Initialize(baseUrl, privateKey, apiKeyIndex, accountIndex);
            _signerInitialized = true;

            _logger.LogDebug(
                "Lighter signer initialized: accountIndex={AccountIndex} apiKeyIndex={ApiKeyIndex}",
                _accountIndex, _apiKeyIndexStr);
        }
    }

    /// <summary>
    /// Get the account index from configuration.
    /// Used for non-trading calls (e.g. GetAvailableBalanceAsync) that run before EnsureSignerReady.
    /// </summary>
    private long GetAccountIndex()
    {
        // If already cached by EnsureSignerReady, return cached value immediately
        if (_signerInitialized)
        {
            return _accountIndex;
        }

        var indexStr = _configuration["Exchanges:Lighter:AccountIndex"];
        if (string.IsNullOrEmpty(indexStr))
        {
            throw new InvalidOperationException(
                "Lighter Account Index is required and must be numeric. Check your API key settings.");
        }

        if (!long.TryParse(indexStr, out var accountIndex))
        {
            throw new InvalidOperationException(
                "Lighter Account Index must be numeric. Check your API key settings.");
        }

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
    /// Polls the tx status endpoint up to 3 times (1-2s apart).
    /// Returns (proceed, status) where:
    ///   - proceed=false, status=0  → tx failed, abort immediately
    ///   - proceed=true,  status=2  → tx executed, proceed to verification
    ///   - proceed=true,  status=1  → still pending after 3 polls, proceed to verification
    ///   - proceed=true,  status=-1 → API error/unavailable, fall back to verification
    /// </summary>
    internal async Task<(bool Proceed, int Status)> CheckTxStatusAsync(string txHash, CancellationToken ct)
    {
        const int maxPolls = 3;

        for (int i = 0; i < maxPolls; i++)
        {
            if (i > 0)
            {
                await Task.Delay(i == 1 ? 1000 : 2000, ct);
            }

            try
            {
                var response = await _httpClient.GetAsync($"tx?hash={Uri.EscapeDataString(txHash)}", ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "TX status check returned {StatusCode} for {TxHash} — falling back to position verification",
                        (int)response.StatusCode, txHash);
                    return (true, -1);
                }

                var txStatus = await response.Content.ReadFromJsonAsync<LighterTxStatusResponse>(JsonOptions, ct);
                if (txStatus is null)
                {
                    _logger.LogDebug("TX status response was null for {TxHash} — falling back", txHash);
                    return (true, -1);
                }

                switch (txStatus.Status)
                {
                    case 0: // Failed
                        _logger.LogWarning(
                            "TX status check: order FAILED for txHash={TxHash} (status=0)",
                            txHash);
                        return (false, 0);

                    case 2: // Executed
                        _logger.LogInformation(
                            "TX status check: order EXECUTED for txHash={TxHash} (status=2)",
                            txHash);
                        return (true, 2);

                    case 1: // Pending — continue polling
                        _logger.LogDebug(
                            "TX status check: PENDING for txHash={TxHash} (poll {Poll}/{Max})",
                            txHash, i + 1, maxPolls);
                        break;

                    default:
                        _logger.LogDebug(
                            "TX status check: unknown status {Status} for txHash={TxHash} — falling back",
                            txStatus.Status, txHash);
                        return (true, -1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "TX status check failed for txHash={TxHash} — falling back to position verification",
                    txHash);
                return (true, -1);
            }
        }

        // Still pending after all polls — proceed to position verification
        _logger.LogInformation(
            "TX status check: still PENDING after {Max} polls for txHash={TxHash} — proceeding to verification",
            maxPolls, txHash);
        return (true, 1);
    }

    /// <summary>
    /// Fetches the cancellation reason for the most recent inactive order on the given market.
    /// Fire-and-forget diagnostic — does not throw on failure.
    /// Cancellation codes: 8=Margin, 9=Slippage, 10=Liquidity, 16=Balance.
    /// </summary>
    internal async Task<string?> GetCancellationReasonAsync(int marketId, CancellationToken ct)
    {
        try
        {
            var accountIndex = GetAccountIndex();
            var response = await _httpClient.GetAsync(
                $"accountInactiveOrders?account_index={accountIndex}", ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content
                .ReadFromJsonAsync<LighterInactiveOrdersResponse>(JsonOptions, ct);

            // Last in API response order = most recent (Lighter returns in insertion order)
            var order = result?.InactiveOrders?
                .Where(o => o.MarketId == marketId)
                .LastOrDefault();

            if (order is null)
            {
                return null;
            }

            var reasonText = order.CancelReason switch
            {
                8 => "Margin insufficient",
                9 => "Slippage tolerance exceeded",
                10 => "Insufficient liquidity",
                16 => "Balance insufficient",
                _ => $"Unknown (code={order.CancelReason})"
            };

            return reasonText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch cancellation reason for market {MarketId}", marketId);
            return null;
        }
    }

    /// <summary>
    /// Submit a signed transaction to the Lighter API via POST /api/v1/sendTx.
    /// </summary>
    internal async Task<LighterSendTxResponse> SendTransactionAsync(
        byte txType, string txInfo, CancellationToken ct)
    {
        // The Lighter API expects multipart/form-data with tx_type and tx_info fields
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(txType.ToString()), "tx_type");
        form.Add(new StringContent(txInfo), "tx_info");

        var response = await _httpClient.PostAsync("sendTx", form, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        _logger.LogDebug(
            "SendTransaction response: statusCode={StatusCode}",
            (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var truncatedBody = (responseBody.Length > 500 ? responseBody[..500] : responseBody)
                .Replace("\r", "").Replace("\n", " ");
            _logger.LogWarning(
                "SendTransaction failed: statusCode={StatusCode} body={Body}",
                (int)response.StatusCode, truncatedBody);
            throw new HttpRequestException(
                $"Lighter order submission failed ({response.StatusCode})");
        }

        var result = JsonSerializer.Deserialize<LighterSendTxResponse>(responseBody, JsonOptions);
        if (result is null)
        {
            throw new InvalidOperationException("Failed to deserialize Lighter sendTx response");
        }

        if (result.Code != 200)
        {
            var sanitizedMessage = (result.Message?.Length > 200
                ? result.Message[..200] : result.Message ?? "")
                .Replace("\r", "").Replace("\n", " ");
            _logger.LogWarning(
                "SendTransaction error code: code={Code} message={Message} txHash={TxHash}",
                result.Code, sanitizedMessage, result.TxHash);
            throw new InvalidOperationException(
                $"Lighter sendTx returned error code {result.Code}: {sanitizedMessage}");
        }

        _logger.LogInformation(
            "SendTransaction success: code={Code} txHash={TxHash}",
            result.Code, result.TxHash);
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
        {
            return currentCache.GetValueOrDefault(asset);
        }

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

    /// <inheritdoc />
    /// <remarks>Lighter settles funding hourly on the hour.</remarks>
    public Task<DateTime?> GetNextFundingTimeAsync(string asset, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        return Task.FromResult<DateTime?>(nextHour);
    }

    public async Task<int?> GetMaxLeverageAsync(string asset, CancellationToken ct = default)
    {
        try
        {
            var market = await GetMarketDetailAsync(asset, ct);
            if (market is null || market.MinInitialMarginFraction <= 0)
            {
                return null;
            }

            // maxLeverage = 1 / (MinInitialMarginFraction / 10000)
            // The MinInitialMarginFraction is stored as basis points (e.g., 1000 = 10%)
            var maxLeverage = (int)(10_000m / market.MinInitialMarginFraction);
            return maxLeverage > 0 ? maxLeverage : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _signer?.Dispose();
        _cacheLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
