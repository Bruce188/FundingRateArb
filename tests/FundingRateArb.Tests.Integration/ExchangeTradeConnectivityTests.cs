using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Domain.Enums;
using FundingRateArb.Infrastructure.ExchangeConnectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using Xunit.Abstractions;

namespace FundingRateArb.Tests.Integration;

[Trait("Category", "TradeConnectivity")]
public class ExchangeTradeConnectivityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ServiceProvider _serviceProvider;
    private readonly ExchangeConnectorFactory _factory;

    // Credential env vars
    private readonly string? _hlWallet = Environment.GetEnvironmentVariable("TRADE_TEST_HL_WALLET");
    private readonly string? _hlKey = Environment.GetEnvironmentVariable("TRADE_TEST_HL_KEY");
    private readonly string? _lighterKey = Environment.GetEnvironmentVariable("TRADE_TEST_LIGHTER_KEY");
    private readonly string? _lighterAccount = Environment.GetEnvironmentVariable("TRADE_TEST_LIGHTER_ACCOUNT");
    private readonly string? _lighterApiKeyIndex = Environment.GetEnvironmentVariable("TRADE_TEST_LIGHTER_API_KEY_INDEX") ?? "2";
    private readonly string? _asterKey = Environment.GetEnvironmentVariable("TRADE_TEST_ASTER_KEY");
    private readonly string? _asterSecret = Environment.GetEnvironmentVariable("TRADE_TEST_ASTER_SECRET");

    private const decimal SizeUsdc = 12m;
    private const int Leverage = 1;
    private const string Asset = "ETH";
    private static readonly Side TradeSide = Side.Long;

    public ExchangeTradeConnectivityTests(ITestOutputHelper output)
    {
        _output = output;
        (_factory, _serviceProvider) = CreateFactory();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    [Trait("Category", "TradeConnectivity")]
    public async Task TradeRoundTrip_Hyperliquid()
    {
        Skip.If(string.IsNullOrEmpty(_hlWallet) || string.IsNullOrEmpty(_hlKey),
            "Hyperliquid credentials not configured (TRADE_TEST_HL_WALLET, TRADE_TEST_HL_KEY)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var connector = await _factory.CreateForUserAsync("hyperliquid", null, null, _hlWallet, _hlKey);
        Assert.NotNull(connector);

        try
        {
            _output.WriteLine("=== Hyperliquid Trade Connectivity Test ===");

            // Check balance
            var balance = await connector.GetAvailableBalanceAsync(ct);
            _output.WriteLine($"[Hyperliquid] Balance: ${balance:F2}");
            Assert.True(balance > 0, "Hyperliquid balance must be > 0");

            // Open position
            var openResult = await connector.PlaceMarketOrderAsync(Asset, TradeSide, SizeUsdc, Leverage, ct);
            _output.WriteLine($"[Hyperliquid] Open: {(openResult.Success ? "SUCCESS" : "FAILED")} " +
                $"OrderId={openResult.OrderId} Price={openResult.FilledPrice} Qty={openResult.FilledQuantity}");
            Assert.True(openResult.Success, $"Hyperliquid open failed: {openResult.Error}");

            // Wait for settlement
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // Close position
            var closeResult = await connector.ClosePositionAsync(Asset, TradeSide, ct);
            _output.WriteLine($"[Hyperliquid] Close: {(closeResult.Success ? "SUCCESS" : "FAILED")} OrderId={closeResult.OrderId}");
            Assert.True(closeResult.Success, $"Hyperliquid close failed: {closeResult.Error}");

            _output.WriteLine("[Hyperliquid] PASS");
        }
        finally
        {
            if (connector is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else
            {
                (connector as IDisposable)?.Dispose();
            }
        }
    }

    [SkippableFact]
    [Trait("Category", "TradeConnectivity")]
    public async Task TradeRoundTrip_Lighter()
    {
        Skip.If(string.IsNullOrEmpty(_lighterKey) || string.IsNullOrEmpty(_lighterAccount),
            "Lighter credentials not configured (TRADE_TEST_LIGHTER_KEY, TRADE_TEST_LIGHTER_ACCOUNT)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var connector = await _factory.CreateForUserAsync(
            "lighter", null, null, _lighterAccount, _lighterKey, null, _lighterApiKeyIndex);
        Assert.NotNull(connector);

        try
        {
            _output.WriteLine("=== Lighter Trade Connectivity Test ===");

            // Check balance
            var balance = await connector.GetAvailableBalanceAsync(ct);
            _output.WriteLine($"[Lighter] Balance: ${balance:F2}");
            Assert.True(balance > 0, "Lighter balance must be > 0");

            // Open position (fire-and-forget tx submission)
            var openResult = await connector.PlaceMarketOrderAsync(Asset, TradeSide, SizeUsdc, Leverage, ct);
            _output.WriteLine($"[Lighter] Open: {(openResult.Success ? "SUCCESS" : "FAILED")} " +
                $"TxHash={openResult.OrderId} (estimated fill: {openResult.IsEstimatedFill})");
            Assert.True(openResult.Success, $"Lighter open failed: {openResult.Error}");

            // Verify position opened on-chain (polls up to 35s for zk-rollup settlement)
            if (connector is IPositionVerifiable verifiable)
            {
                var verified = await verifiable.VerifyPositionOpenedAsync(Asset, TradeSide, ct);
                _output.WriteLine($"[Lighter] Verify: {(verified ? "Position confirmed" : "Position NOT confirmed")}");
                Assert.True(verified, "Lighter position verification failed — position did not appear on-chain");
            }
            else
            {
                _output.WriteLine("[Lighter] Verify: SKIPPED (connector does not implement IPositionVerifiable)");
            }

            // Close position
            var closeResult = await connector.ClosePositionAsync(Asset, TradeSide, ct);
            _output.WriteLine($"[Lighter] Close: {(closeResult.Success ? "SUCCESS" : "FAILED")} TxHash={closeResult.OrderId}");
            Assert.True(closeResult.Success, $"Lighter close failed: {closeResult.Error}");

            _output.WriteLine("[Lighter] PASS");
        }
        finally
        {
            if (connector is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else
            {
                (connector as IDisposable)?.Dispose();
            }
        }
    }

    [SkippableFact]
    [Trait("Category", "TradeConnectivity")]
    public async Task TradeRoundTrip_Aster()
    {
        Skip.If(string.IsNullOrEmpty(_asterKey) || string.IsNullOrEmpty(_asterSecret),
            "Aster credentials not configured (TRADE_TEST_ASTER_KEY, TRADE_TEST_ASTER_SECRET)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        var connector = await _factory.CreateForUserAsync("aster", _asterKey, _asterSecret, null, null);
        Assert.NotNull(connector);

        try
        {
            _output.WriteLine("=== Aster Trade Connectivity Test ===");

            // Check balance
            var balance = await connector.GetAvailableBalanceAsync(ct);
            _output.WriteLine($"[Aster] Balance: ${balance:F2}");
            Assert.True(balance > 0, "Aster balance must be > 0");

            // Open position
            var openResult = await connector.PlaceMarketOrderAsync(Asset, TradeSide, SizeUsdc, Leverage, ct);
            _output.WriteLine($"[Aster] Open: {(openResult.Success ? "SUCCESS" : "FAILED")} " +
                $"OrderId={openResult.OrderId} Price={openResult.FilledPrice} Qty={openResult.FilledQuantity}");
            Assert.True(openResult.Success, $"Aster open failed: {openResult.Error}");

            // Wait for settlement
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            // Close position
            var closeResult = await connector.ClosePositionAsync(Asset, TradeSide, ct);
            _output.WriteLine($"[Aster] Close: {(closeResult.Success ? "SUCCESS" : "FAILED")} OrderId={closeResult.OrderId}");
            Assert.True(closeResult.Success, $"Aster close failed: {closeResult.Error}");

            _output.WriteLine("[Aster] PASS");
        }
        finally
        {
            if (connector is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else
            {
                (connector as IDisposable)?.Dispose();
            }
        }
    }

    [SkippableFact]
    [Trait("Category", "TradeConnectivity")]
    public async Task TradeRoundTrip_AllExchanges()
    {
        var hasHl = !string.IsNullOrEmpty(_hlWallet) && !string.IsNullOrEmpty(_hlKey);
        var hasLighter = !string.IsNullOrEmpty(_lighterKey) && !string.IsNullOrEmpty(_lighterAccount);
        var hasAster = !string.IsNullOrEmpty(_asterKey) && !string.IsNullOrEmpty(_asterSecret);

        Skip.If(!hasHl && !hasLighter && !hasAster,
            "No exchange credentials configured — skipping all-exchange test");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = cts.Token;

        _output.WriteLine("=== Exchange Trade Connectivity Test ===");

        var results = new Dictionary<string, string>();
        var failures = new List<string>();

        // Hyperliquid
        if (hasHl)
        {
            try
            {
                var connector = await _factory.CreateForUserAsync("hyperliquid", null, null, _hlWallet, _hlKey);
                Assert.NotNull(connector);

                try
                {
                    var balance = await connector.GetAvailableBalanceAsync(ct);
                    _output.WriteLine($"[Hyperliquid] Balance: ${balance:F2}");

                    var openResult = await connector.PlaceMarketOrderAsync(Asset, TradeSide, SizeUsdc, Leverage, ct);
                    _output.WriteLine($"[Hyperliquid] Open: {(openResult.Success ? "SUCCESS" : "FAILED")} " +
                        $"OrderId={openResult.OrderId} Price={openResult.FilledPrice} Qty={openResult.FilledQuantity}");

                    if (!openResult.Success)
                    {
                        throw new Exception($"Open failed: {openResult.Error}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), ct);

                    var closeResult = await connector.ClosePositionAsync(Asset, TradeSide, ct);
                    _output.WriteLine($"[Hyperliquid] Close: {(closeResult.Success ? "SUCCESS" : "FAILED")} OrderId={closeResult.OrderId}");

                    if (!closeResult.Success)
                    {
                        throw new Exception($"Close failed: {closeResult.Error}");
                    }

                    _output.WriteLine("[Hyperliquid] PASS");
                    results["Hyperliquid"] = "PASS";
                }
                finally
                {
                    if (connector is IAsyncDisposable ad)
                    {
                        await ad.DisposeAsync();
                    }
                    else
                    {
                        (connector as IDisposable)?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[Hyperliquid] FAIL: {ex.Message}");
                results["Hyperliquid"] = $"FAIL: {ex.Message}";
                failures.Add("Hyperliquid");
            }
        }
        else
        {
            _output.WriteLine("[Hyperliquid] SKIP: credentials not configured");
            results["Hyperliquid"] = "SKIP";
        }

        _output.WriteLine("");

        // Lighter
        if (hasLighter)
        {
            try
            {
                var connector = await _factory.CreateForUserAsync(
                    "lighter", null, null, _lighterAccount, _lighterKey, null, _lighterApiKeyIndex);
                Assert.NotNull(connector);

                try
                {
                    var balance = await connector.GetAvailableBalanceAsync(ct);
                    _output.WriteLine($"[Lighter] Balance: ${balance:F2}");

                    var openResult = await connector.PlaceMarketOrderAsync(Asset, TradeSide, SizeUsdc, Leverage, ct);
                    _output.WriteLine($"[Lighter] Open: {(openResult.Success ? "SUCCESS" : "FAILED")} " +
                        $"TxHash={openResult.OrderId} (estimated fill: {openResult.IsEstimatedFill})");

                    if (!openResult.Success)
                    {
                        throw new Exception($"Open failed: {openResult.Error}");
                    }

                    if (connector is IPositionVerifiable verifiable)
                    {
                        var verified = await verifiable.VerifyPositionOpenedAsync(Asset, TradeSide, ct);
                        _output.WriteLine($"[Lighter] Verify: {(verified ? "Position confirmed" : "Position NOT confirmed")}");
                        if (!verified)
                        {
                            throw new Exception("Position verification failed");
                        }
                    }

                    var closeResult = await connector.ClosePositionAsync(Asset, TradeSide, ct);
                    _output.WriteLine($"[Lighter] Close: {(closeResult.Success ? "SUCCESS" : "FAILED")} TxHash={closeResult.OrderId}");

                    if (!closeResult.Success)
                    {
                        throw new Exception($"Close failed: {closeResult.Error}");
                    }

                    _output.WriteLine("[Lighter] PASS");
                    results["Lighter"] = "PASS";
                }
                finally
                {
                    if (connector is IAsyncDisposable ad)
                    {
                        await ad.DisposeAsync();
                    }
                    else
                    {
                        (connector as IDisposable)?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[Lighter] FAIL: {ex.Message}");
                results["Lighter"] = $"FAIL: {ex.Message}";
                failures.Add("Lighter");
            }
        }
        else
        {
            _output.WriteLine("[Lighter] SKIP: credentials not configured");
            results["Lighter"] = "SKIP";
        }

        _output.WriteLine("");

        // Aster
        if (hasAster)
        {
            try
            {
                var connector = await _factory.CreateForUserAsync("aster", _asterKey, _asterSecret, null, null);
                Assert.NotNull(connector);

                try
                {
                    var balance = await connector.GetAvailableBalanceAsync(ct);
                    _output.WriteLine($"[Aster] Balance: ${balance:F2}");

                    var openResult = await connector.PlaceMarketOrderAsync(Asset, TradeSide, SizeUsdc, Leverage, ct);
                    _output.WriteLine($"[Aster] Open: {(openResult.Success ? "SUCCESS" : "FAILED")} " +
                        $"OrderId={openResult.OrderId} Price={openResult.FilledPrice} Qty={openResult.FilledQuantity}");

                    if (!openResult.Success)
                    {
                        throw new Exception($"Open failed: {openResult.Error}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), ct);

                    var closeResult = await connector.ClosePositionAsync(Asset, TradeSide, ct);
                    _output.WriteLine($"[Aster] Close: {(closeResult.Success ? "SUCCESS" : "FAILED")} OrderId={closeResult.OrderId}");

                    if (!closeResult.Success)
                    {
                        throw new Exception($"Close failed: {closeResult.Error}");
                    }

                    _output.WriteLine("[Aster] PASS");
                    results["Aster"] = "PASS";
                }
                finally
                {
                    if (connector is IAsyncDisposable ad)
                    {
                        await ad.DisposeAsync();
                    }
                    else
                    {
                        (connector as IDisposable)?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[Aster] FAIL: {ex.Message}");
                results["Aster"] = $"FAIL: {ex.Message}";
                failures.Add("Aster");
            }
        }
        else
        {
            _output.WriteLine("[Aster] SKIP: credentials not configured");
            results["Aster"] = "SKIP";
        }

        // Summary
        _output.WriteLine("");
        _output.WriteLine("=== Summary ===");
        foreach (var (exchange, result) in results)
        {
            _output.WriteLine($"{exchange,-12} {result}");
        }

        Assert.True(failures.Count == 0,
            $"Trade connectivity failed on: {string.Join(", ", failures)}");
    }

    private static (ExchangeConnectorFactory Factory, ServiceProvider Provider) CreateFactory()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // Polly resilience pipelines matching production configuration
        services.AddResiliencePipeline("ExchangeSdk", static pipelineBuilder =>
        {
            pipelineBuilder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TaskCanceledException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            });

            pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>(),
            });

            pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(15));
        });

        services.AddResiliencePipeline("OrderExecution", static pipelineBuilder =>
        {
            pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 3,
                BreakDuration = TimeSpan.FromSeconds(60),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>(),
            });

            pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(30));
        });

        services.AddResiliencePipeline("OrderClose", static pipelineBuilder =>
        {
            pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(30));
        });

        var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILogger<ExchangeConnectorFactory>>();

        return (new ExchangeConnectorFactory(sp, logger), sp);
    }
}
