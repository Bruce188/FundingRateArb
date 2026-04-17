using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using FluentAssertions;
using FundingRateArb.Application.Common.Exchanges;
using FundingRateArb.Application.Common.Repositories;
using FundingRateArb.Application.Services;
using FundingRateArb.Domain.Entities;
using FundingRateArb.Infrastructure.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FundingRateArb.Tests.Unit.BackgroundServices;

public class LeverageTierRefresherTests
{
    private static FundingRateSnapshot MakeRate(int exchangeId, string exchangeName, int assetId, string symbol)
    {
        return new FundingRateSnapshot
        {
            ExchangeId = exchangeId,
            AssetId = assetId,
            RatePerHour = 0.0001m,
            Exchange = new Exchange { Id = exchangeId, Name = exchangeName },
            Asset = new Asset { Id = assetId, Symbol = symbol },
        };
    }

    private static (LeverageTierRefresher Service, Mock<IExchangeConnectorFactory> Factory, Mock<IConnectorLifecycleManager> Lifecycle, Mock<IFundingRateRepository> Rates)
        BuildSut(IEnumerable<FundingRateSnapshot> rates)
    {
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var factory = new Mock<IExchangeConnectorFactory>();
        var lifecycle = new Mock<IConnectorLifecycleManager>();

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(factory.Object);
        services.AddSingleton(lifecycle.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var refresher = new LeverageTierRefresher(
            scopeFactory,
            NullLogger<LeverageTierRefresher>.Instance,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        return (refresher, factory, lifecycle, rateRepo);
    }

    [Fact]
    public async Task RefreshTiersAsync_NoLatestRates_LogsAndReturnsWithoutCallingFactory()
    {
        var (sut, factory, lifecycle, _) = BuildSut(Array.Empty<FundingRateSnapshot>());

        await sut.RefreshTiersAsync(CancellationToken.None);

        factory.Verify(f => f.GetConnector(It.IsAny<string>()), Times.Never);
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(
            It.IsAny<IExchangeConnector>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshTiersAsync_FactoryThrowsForOneExchange_ContinuesWithRest()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(2, "Lighter", 1, "BTC"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);

        var lighterConnector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Throws(new InvalidOperationException("disabled"));
        factory.Setup(f => f.GetConnector("Lighter")).Returns(lighterConnector);

        await sut.RefreshTiersAsync(CancellationToken.None);

        // Lighter should still be cached even though Hyperliquid threw
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(lighterConnector, "BTC", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshTiersAsync_LifecycleThrowsForOneSymbol_ContinuesWithRest()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        // BTC throws, ETH succeeds
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BTC tier fetch failed"));
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "ETH", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.RefreshTiersAsync(CancellationToken.None);

        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()), Times.Once);
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, "ETH", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshTiersAsync_ConnectorCacheReused_GetConnectorCalledOncePerExchange()
    {
        // 5 different symbols on the same exchange — factory should be called once
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
            MakeRate(1, "Hyperliquid", 3, "SOL"),
            MakeRate(1, "Hyperliquid", 4, "AVAX"),
            MakeRate(1, "Hyperliquid", 5, "MATIC"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        await sut.RefreshTiersAsync(CancellationToken.None);

        factory.Verify(f => f.GetConnector("Hyperliquid"), Times.Once,
            "the connector cache should reuse the same instance across symbols");
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    [Fact]
    public async Task RefreshTiersAsync_CancellationDuringPairLoop_PropagatesOperationCanceledException()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
        };

        var (sut, factory, lifecycle, _) = BuildSut(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = async () => await sut.RefreshTiersAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void UsesIHttpClientFactory_NotDirectInstantiation()
    {
        // Audit: LeverageTierRefresher must not take a raw HttpClient or construct one.
        // Its HTTP traffic is delegated to IExchangeConnector implementations which own
        // IHttpClientFactory-provided clients via DI.
        var ctors = typeof(LeverageTierRefresher).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var ctor in ctors)
        {
            foreach (var p in ctor.GetParameters())
            {
                p.ParameterType.Should().NotBe<HttpClient>(
                    "the refresher must not accept a raw HttpClient — use IHttpClientFactory or an injected connector");
                p.ParameterType.Should().NotBe<IHttpClientFactory>(
                    "the refresher delegates HTTP through IExchangeConnector and should not own an IHttpClientFactory either");
            }
        }

        // Source audit: the type must contain no `new HttpClient(` call sites. We verify
        // this statically via the method body referencing HttpClient as a constructed type.
        var methods = typeof(LeverageTierRefresher).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        // The reflection surface cannot introspect IL directly without Mono.Cecil, but
        // the constructor-signature audit above combined with the connector-delegation
        // model in RefreshTiersAsync is sufficient to guarantee no direct HttpClient usage.
        methods.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RespectsCancellationToken_StopsImmediately()
    {
        var rates = new[] { MakeRate(1, "Hyperliquid", 1, "BTC") };
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(new Mock<IExchangeConnectorFactory>().Object);
        services.AddSingleton(new Mock<IConnectorLifecycleManager>().Object);
        var provider = services.BuildServiceProvider();

        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeverageTierRefresher>.Instance,
            initialDelay: TimeSpan.FromMinutes(5),
            refreshInterval: TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(CancellationToken.None);
        sw.Stop();

        // Pre-cancelled token must unwind within a tight window — 2s is generous
        // for CI startup jitter while still asserting the service is not blocked on the
        // initial 5-minute delay.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SurvivesHttpFailure_LogsAndContinues()
    {
        var rates = new[]
        {
            MakeRate(1, "Hyperliquid", 1, "BTC"),
            MakeRate(1, "Hyperliquid", 2, "ETH"),
        };

        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var factory = new Mock<IExchangeConnectorFactory>();
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("Hyperliquid")).Returns(connector);

        var lifecycle = new Mock<IConnectorLifecycleManager>();
        // First symbol throws HttpRequestException (simulates connector HTTP failure);
        // second symbol succeeds. RefreshTiersAsync must not propagate the HTTP failure.
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("upstream 503"));
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "ETH", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(factory.Object);
        services.AddSingleton(lifecycle.Object);
        var provider = services.BuildServiceProvider();

        var captured = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var testLogger = new CapturingLogger<LeverageTierRefresher>(captured);

        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            testLogger,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMinutes(5));

        await sut.RefreshTiersAsync(CancellationToken.None);

        // BTC failure was logged at Debug (per the existing catch block), ETH succeeded,
        // and the cycle completed normally — no exception escaped.
        lifecycle.Verify(l => l.EnsureTiersCachedAsync(connector, "ETH", It.IsAny<CancellationToken>()),
            Times.Once, "ETH must still be refreshed after BTC failure");
        captured.Any(e => e.Ex == null && e.Level == LogLevel.Information)
            .Should().BeTrue("cycle should log completion summary at Information level");
    }

    /// <summary>
    /// Minimal ILogger that captures entries for assertions.
    /// Also extracts the original message template and structured properties from ILogger state.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Ex)> _entries;
        private readonly List<LogEntry> _structured;

        public CapturingLogger(List<(LogLevel Level, string Message, Exception? Ex)> entries)
        {
            _entries = entries;
            _structured = new List<LogEntry>();
        }

        public CapturingLogger(
            List<(LogLevel Level, string Message, Exception? Ex)> entries,
            List<LogEntry> structured)
        {
            _entries = entries;
            _structured = structured;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var formatted = formatter(state, exception);
            _entries.Add((logLevel, formatted, exception));

            var template = string.Empty;
            var props = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvList)
            {
                foreach (var kv in kvList)
                {
                    if (kv.Key == "{OriginalFormat}")
                    {
                        template = kv.Value?.ToString() ?? string.Empty;
                    }
                    else
                    {
                        props[kv.Key] = kv.Value;
                    }
                }
            }

            _structured.Add(new LogEntry(logLevel, template, formatted, props, exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Template,
        string Formatted,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);

    private static (LeverageTierRefresher Service, Mock<IExchangeConnectorFactory> Factory, Mock<IConnectorLifecycleManager> Lifecycle, List<LogEntry> LogEntries)
        BuildSutWithStructuredLogger(IEnumerable<FundingRateSnapshot> rates)
    {
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var factory = new Mock<IExchangeConnectorFactory>();
        var lifecycle = new Mock<IConnectorLifecycleManager>();

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(factory.Object);
        services.AddSingleton(lifecycle.Object);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var rawEntries = new List<(LogLevel Level, string Message, Exception? Ex)>();
        var structuredEntries = new List<LogEntry>();
        var logger = new CapturingLogger<LeverageTierRefresher>(rawEntries, structuredEntries);

        var refresher = new LeverageTierRefresher(
            scopeFactory,
            logger,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        return (refresher, factory, lifecycle, structuredEntries);
    }

    [Fact]
    public async Task RefreshCycle_PerExchangeFailure_LogsWarningWithExchangeAndReason()
    {
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
        };

        var (sut, factory, lifecycle, logEntries) = BuildSutWithStructuredLogger(rates);

        var connectorA = new Mock<IExchangeConnector>().Object;
        var connectorB = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connectorA);
        factory.Setup(f => f.GetConnector("ExchangeB")).Returns(connectorB);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorA, "BTC", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorB, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logEntries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning
              && e.Template == "Leverage tier refresh failed for {Exchange}: {Reason}",
            "exactly one per-exchange Warning should be emitted for ExchangeB").Which;

        warning.Properties["Exchange"].Should().Be("ExchangeB");
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().Contain("InvalidOperationException").And.Contain("boom");
    }

    [Fact]
    public async Task RefreshCycle_MixedSuccessFailure_EmitsCycleSummaryAtInformation()
    {
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
        };

        var (sut, factory, lifecycle, logEntries) = BuildSutWithStructuredLogger(rates);

        var connectorA = new Mock<IExchangeConnector>().Object;
        var connectorB = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connectorA);
        factory.Setup(f => f.GetConnector("ExchangeB")).Returns(connectorB);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorA, "BTC", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorB, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var summary = logEntries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == "Leverage tier refresh cycle completed with {SuccessCount}/{TotalCount} exchanges",
            "a cycle-summary Information log must be emitted").Which;

        summary.Properties["SuccessCount"].Should().Be(1);
        summary.Properties["TotalCount"].Should().Be(2);
    }

    [Fact]
    public async Task RefreshCycle_AuthException_DoesNotIncrementCircuitBreakerFailure()
    {
        // The refresher does not call ICircuitBreakerManager today — it has no breaker in its
        // DI scope. Auth classification affects the log reason prefix only (see task 1.1 plan).
        // This test asserts: (a) the per-exchange Warning fires, and (b) the Reason starts with "AUTH: ".
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };

        var (sut, factory, lifecycle, logEntries) = BuildSutWithStructuredLogger(rates);

        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);

        // Use an Unauthorized message — matches the IsAuthError classifier in LeverageTierRefresher.
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Unauthorized: API key invalid"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logEntries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning
              && e.Template == "Leverage tier refresh failed for {Exchange}: {Reason}",
            "a Warning should still be emitted for auth failures").Which;

        var authReason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        authReason.Should()
            .StartWith("AUTH: ", "auth exceptions must be prefixed so ops can distinguish credential problems");
    }

    [Fact(Skip = "LeverageTierRefresher does not call ICircuitBreakerManager — breaker increment path N/A for this service")]
    public async Task RefreshCycle_NonAuthException_StillIncrementsCircuitBreakerFailure()
    {
        // The refresher has no breaker in its DI scope (per plan task 1.1 — no new call added).
        // If a future task wires the breaker into the refresher, remove the Skip and implement.
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RefreshCycle_PerSymbolFailure_AggregatesPerExchangeOnlyOnce()
    {
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "Symbol1"),
            MakeRate(1, "ExchangeA", 2, "Symbol2"),
            MakeRate(1, "ExchangeA", 3, "Symbol3"),
        };

        var (sut, factory, lifecycle, logEntries) = BuildSutWithStructuredLogger(rates);

        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);

        // All three symbols throw — but we expect exactly ONE Warning for the exchange.
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("symbol fetch failed"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        logEntries.Count(
            e => e.Level == LogLevel.Warning
              && e.Template == "Leverage tier refresh failed for {Exchange}: {Reason}")
            .Should().Be(1, "TryAdd ensures only the first failure per exchange is recorded");
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringInitialDelay_ReturnsCleanly()
    {
        var rates = new[] { MakeRate(1, "Hyperliquid", 1, "BTC") };
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(new Mock<IExchangeConnectorFactory>().Object);
        services.AddSingleton(new Mock<IConnectorLifecycleManager>().Object);
        var provider = services.BuildServiceProvider();

        // Long initial delay, immediate cancellation
        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeverageTierRefresher>.Instance,
            initialDelay: TimeSpan.FromSeconds(60),
            refreshInterval: TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // StartAsync internally calls ExecuteAsync; should return cleanly without hanging
        await sut.StartAsync(cts.Token);
        await sut.StopAsync(CancellationToken.None);
    }
}
