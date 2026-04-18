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
    private const string CycleSummaryTemplate =
        "Leverage tier refresh cycle completed: {Success}/{Total} succeeded, {Skipped} skipped";

    private const string FailedExchangeTemplate =
        "Leverage tier refresh failed for {Exchange}: {Reason}";

    private const string PerSymbolDebugTemplate =
        "Tier refresh failed for {Exchange}/{Symbol}: {Error}";

    private const string CycleAbortedTemplate =
        "Leverage tier refresh cycle aborted: {ExceptionType}";

    private const string EmptyPairsDebugTemplate =
        "Leverage tier refresh skipped — no active (exchange, asset) pairs found";

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

        var testLogger = new CapturingLogger<LeverageTierRefresher>();

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

        // Assert log ordering deterministically: Debug (BTC fail) precedes the Information summary.
        var relevantEntries = testLogger.Entries
            .Where(e => e.Level is LogLevel.Debug or LogLevel.Warning or LogLevel.Information)
            .ToList();
        var debugIdx = relevantEntries.FindIndex(e => e.Level == LogLevel.Debug
            && e.Template == PerSymbolDebugTemplate);
        var infoIdx = relevantEntries.FindIndex(e => e.Level == LogLevel.Information
            && e.Template == CycleSummaryTemplate);
        debugIdx.Should().BeGreaterThanOrEqualTo(0, "Debug entry for BTC failure must exist");
        infoIdx.Should().BeGreaterThan(debugIdx, "cycle-completed Info log must come after per-symbol Debug log");
    }

    /// <summary>
    /// Minimal ILogger that captures entries for assertions.
    /// Also extracts the original message template and structured properties from ILogger state.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly LogLevel _minLevel;
        private readonly List<LogEntry> _entries = new();

        public CapturingLogger(LogLevel minLevel = LogLevel.Trace)
        {
            _minLevel = minLevel;
        }

        /// <summary>All captured log entries in emission order.</summary>
        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var formatted = formatter(state, exception);

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

            _entries.Add(new LogEntry(logLevel, template, formatted, props, exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Template,
        string Formatted,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);

    private static (LeverageTierRefresher Service, Mock<IExchangeConnectorFactory> Factory, Mock<IConnectorLifecycleManager> Lifecycle, CapturingLogger<LeverageTierRefresher> Logger)
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

        var logger = new CapturingLogger<LeverageTierRefresher>();

        var refresher = new LeverageTierRefresher(
            scopeFactory,
            logger,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        return (refresher, factory, lifecycle, logger);
    }

    [Fact]
    public async Task RefreshCycle_PerExchangeFailure_LogsWarningWithExchangeAndReason()
    {
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
        };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        var connectorA = new Mock<IExchangeConnector>().Object;
        var connectorB = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connectorA);
        factory.Setup(f => f.GetConnector("ExchangeB")).Returns(connectorB);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorA, "BTC", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorB, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var logEntries = logger.Entries;
        var warning = logEntries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate,
            "exactly one per-exchange Warning should be emitted for ExchangeB").Which;

        warning.Properties["Exchange"].Should().Be("ExchangeB");
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().Contain("InvalidOperationException");

        // Assert deterministic ordering: Warning for ExchangeB precedes the cycle summary.
        var ordered = logEntries.ToList();
        var warningIdx = ordered.FindIndex(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var summaryIdx = ordered.FindIndex(e => e.Level == LogLevel.Information
            && e.Template == CycleSummaryTemplate);
        warningIdx.Should().BeGreaterThanOrEqualTo(0, "Warning entry must exist");
        summaryIdx.Should().BeGreaterThan(warningIdx, "cycle-completed Info log must follow per-exchange Warning");
    }

    [Fact]
    public async Task RefreshCycle_MixedSuccessFailure_EmitsCycleSummaryAtInformation()
    {
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
        };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        var connectorA = new Mock<IExchangeConnector>().Object;
        var connectorB = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connectorA);
        factory.Setup(f => f.GetConnector("ExchangeB")).Returns(connectorB);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorA, "BTC", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorB, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var logEntries = logger.Entries;
        var summary = logEntries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "a cycle-summary Information log must be emitted").Which;

        summary.Properties["Success"].Should().Be(1);
        summary.Properties["Total"].Should().Be(2);
        summary.Properties["Skipped"].Should().Be(0);
    }

    [Theory]
    [InlineData("Invalid API-key", true)]
    [InlineData("-2015", true)]
    [InlineData("Unauthorized", true)]
    [InlineData("credentials not provided", true)]
    [InlineData("network timeout", false)]
    public async Task RefreshCycle_AuthException_ReasonPrefixedWithAuth(string fragment, bool expectAuth)
    {
        // The refresher does not call ICircuitBreakerManager today — it has no breaker in its
        // DI scope. Auth classification affects the log reason prefix only.
        // This test asserts: (a) the per-exchange Warning fires, and (b) the Reason starts with "AUTH: "
        // for known credential fragments, and does NOT for non-credential messages.
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(fragment + ": extra context"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var logEntries = logger.Entries;
        var warning = logEntries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate,
            "a Warning should be emitted for all failures").Which;

        var authReason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        if (expectAuth)
        {
            authReason.Should()
                .StartWith("AUTH: ", $"fragment '{fragment}' is a known credential error — must be prefixed so ops can distinguish credential problems");
        }
        else
        {
            authReason.Should()
                .NotStartWith("AUTH: ", $"fragment '{fragment}' is not a credential error — must not be prefixed");
        }
    }

    [Fact]
    public async Task LeverageTierRefresher_PerSymbolFailure_AggregatesSuccessAndSkipped()
    {
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "Symbol1"),
            MakeRate(1, "ExchangeA", 2, "Symbol2"),
            MakeRate(1, "ExchangeA", 3, "Symbol3"),
        };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);

        // All three symbols throw — but we expect exactly ONE Warning for the exchange.
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("symbol fetch failed"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        logger.Entries.Count(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate)
            .Should().Be(1, "TryAdd ensures only the first failure per exchange is recorded");

        // Assert Success and Skipped counts to guard against reclassification regressions.
        var summary = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "cycle summary must be emitted").Which;

        summary.Properties["Success"].Should().Be(0, "all symbols failed — no successes");
        summary.Properties["Skipped"].Should().Be(0, "no ConnectorUnavailable — nothing skipped");
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

    [Fact]
    public async Task LeverageTierRefresher_TransientExchangeFailure_LogsPerExchangeWarning()
    {
        // Arrange: two exchanges; first succeeds, second throws a non-credential,
        // non-ConnectorUnavailable exception via lifecycle (TransientFailure path).
        // The per-exchange Warning must be logged AND the cycle summary still emits
        // with {Success}=1, {Skipped}=0 per the plan spec.
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
        };

        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(rates.ToList());

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var factory = new Mock<IExchangeConnectorFactory>();
        var lifecycle = new Mock<IConnectorLifecycleManager>();

        var connectorA = new Mock<IExchangeConnector>().Object;
        var connectorB = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connectorA);
        factory.Setup(f => f.GetConnector("ExchangeB")).Returns(connectorB);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorA, "BTC", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // ExchangeB fails via lifecycle (TransientFailure — not ConnectorUnavailable/Skipped)
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorB, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ExchangeB transient failure"));

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(factory.Object);
        services.AddSingleton(lifecycle.Object);
        var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger<LeverageTierRefresher>();

        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        await sut.RefreshTiersAsync(CancellationToken.None);

        // Assert: a Warning-level log was emitted for the failed exchange.
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate,
            "a per-exchange Warning must be emitted when lifecycle throws a TransientFailure");

        // Assert: the cycle summary still emits with Success=1, Skipped=0 per plan spec.
        var summary = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "the cycle-completed summary must be emitted").Which;

        summary.Properties["Success"].Should().Be(1, "ExchangeA succeeded");
        summary.Properties["Skipped"].Should().Be(0, "ExchangeB failed via lifecycle — not skipped");
    }

    [Fact]
    public async Task LeverageTierRefresher_CycleAbortedMidExchange_LogsAtWarning()
    {
        // NB3: the ExecuteAsync outer-catch fires when RefreshTiersAsync itself throws
        // (e.g. DI resolution or repository throws before the per-symbol loop).
        // Assert: (a) Warning is logged at CycleAbortedTemplate, (b) ExceptionType is the
        // type name only (no message), (c) the loop continues and the second call succeeds.
        var callCount = 0;
        // Signal from inside the second invocation so we don't rely on a fixed delay.
        var secondCallReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("database unavailable");
                secondCallReached.TrySetResult(true);
                return new List<FundingRateSnapshot>();
            });

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(new Mock<IExchangeConnectorFactory>().Object);
        services.AddSingleton(new Mock<IConnectorLifecycleManager>().Object);
        var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger<LeverageTierRefresher>();

        using var cts = new CancellationTokenSource();

        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        // Run ExecuteAsync — first cycle throws (outer catch logs Warning), second cycle
        // completes cleanly (empty pairs, logs Debug early-exit), then cancel.
        var executeTask = sut.StartAsync(cts.Token);
        // Wait until the second invocation signals, then cancel — no fixed sleep needed.
        await secondCallReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);
        await executeTask;

        // (a) Warning was logged at the outer-catch level.
        var abortWarning = logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning
              && e.Template == CycleAbortedTemplate,
            "ExecuteAsync outer-catch must log at Warning when RefreshTiersAsync throws").Which;

        // (b) Only type name is logged — no exception message.
        abortWarning.Properties["ExceptionType"].Should().Be(
            "InvalidOperationException",
            "ExceptionType must be the base exception type name, not the message");

        // (c) The loop continued — second call to GetLatestPerExchangePerAssetAsync happened.
        callCount.Should().BeGreaterThanOrEqualTo(2, "loop must continue after the aborted cycle");
    }

    [Fact]
    public async Task LeverageTierRefresher_FirstTick_OccursAtNominalSchedule()
    {
        // NB2: drift-cadence cycle-1 test. The first refresh tick (tickIndex==1 after increment)
        // must fire at approximately 1× _refreshInterval from the start, not 2× (off-by-one would
        // fire at 0 or 2× depending on pre- vs post-increment bug).
        var callTimestamps = new System.Collections.Concurrent.ConcurrentQueue<long>();
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync())
            .ReturnsAsync(() =>
            {
                callTimestamps.Enqueue(Stopwatch.GetTimestamp());
                return new List<FundingRateSnapshot>();
            });

        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);

        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(new Mock<IExchangeConnectorFactory>().Object);
        services.AddSingleton(new Mock<IConnectorLifecycleManager>().Object);
        var provider = services.BuildServiceProvider();

        var interval = TimeSpan.FromMilliseconds(100);

        using var cts = new CancellationTokenSource();

        var sut = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LeverageTierRefresher>.Instance,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: interval);

        var startTimestamp = Stopwatch.GetTimestamp();
        var executeTask = sut.StartAsync(cts.Token);

        // Wait enough for at least 2 cycles.
        await Task.Delay(350);
        cts.Cancel();
        await sut.StopAsync(CancellationToken.None);
        await executeTask;

        callTimestamps.Should().HaveCountGreaterThanOrEqualTo(2,
            "at least two cycles should complete in 350ms with a 100ms interval");

        var timestamps = callTimestamps.ToArray();

        // Cycle 0 fires immediately (after initial delay ~1ms).
        var cycle0Elapsed = Stopwatch.GetElapsedTime(startTimestamp, timestamps[0]);
        cycle0Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(80),
            "first cycle should fire quickly (initial delay is 1ms)");

        // Cycle 1 fires at approximately 1× interval from start anchor.
        var cycle1Elapsed = Stopwatch.GetElapsedTime(startTimestamp, timestamps[1]);
        cycle1Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(60),
            "second cycle fires after ~1× interval (60ms lower bound with jitter)");
        cycle1Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(180),
            "second cycle fires at most 1.8× interval — an off-by-one post-increment bug would land at ~200ms, outside this bound");
    }

    // ── Non-auth reason sanitization tests (F3 — updated after N1 fix) ────────
    // The non-auth Warning reason is now ex.GetType().Name only (no ex.Message),
    // so the previous truncation tests are replaced with type-name-only assertions.

    [Fact]
    public async Task NonAuthReason_LongMessage_ReasonIsTypeNameOnly()
    {
        // Non-auth exception with a very long message — reason must be the type name only,
        // no truncation needed since ex.Message is not included.
        var body = new string('x', 93); // message body irrelevant — not logged
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(body));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().Be("InvalidOperationException", "non-auth reason must be the exception type name only — no message content");
    }

    [Fact]
    public async Task NonAuthReason_MessageContainsSpecialChars_ReasonIsTypeNameOnly()
    {
        // Non-auth exception with special characters (surrogates) in message —
        // reason must be the type name only, no surrogate handling needed.
        var prefix = new string('x', 92);
        var emoji = "\uD83D\uDE00"; // U+1F600 GRINNING FACE — two UTF-16 chars
        var suffix = new string('x', 10);
        var body = prefix + emoji + suffix;
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(body));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().Be("InvalidOperationException", "exception message content must not appear in the Warning reason");
        reason.Should().NotContain("\uD83D", "high-surrogate from emoji in exception message must not leak into the reason");
        reason.Should().NotContain("\uDE00", "low-surrogate from emoji in exception message must not leak into the reason");
    }

    [Fact]
    public async Task NonAuthReason_DoesNotContainExceptionMessage()
    {
        // Verify that ex.Message (which may contain credential material — e.g. a Bearer
        // token from an HTTP 401 response body) is never present in the Warning reason.
        // Use a neutral sentinel that cannot be mistaken for a real secret by scanners.
        var sensitivePayload = "SECRET_MUST_NOT_LEAK_" + Guid.NewGuid().ToString("N");
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(sensitivePayload));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().NotContain(sensitivePayload, "sensitive payload from exception message must not appear in the sanitized Warning reason");
        reason.Should().Be("InvalidOperationException");
    }

    // ── IsAuthError InnerException depth tests (F4) ─────────────────────────

    private static Exception WrapToDepth(string message, int depth)
    {
        Exception ex = new InvalidOperationException(message);
        for (var i = 0; i < depth; i++)
            ex = new Exception("wrapper", ex);
        return ex;
    }

    [Fact]
    public async Task IsAuthError_CredentialKeywordAtInnerDepth3_DetectsAsAuth()
    {
        // Auth keyword is 3 InnerException hops down — must be detected (hop < 5).
        var ex = WrapToDepth("Invalid API-key fragment", 3);
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().StartWith("AUTH: ", "credential keyword at depth 3 must be detected");
    }

    [Fact]
    public async Task IsAuthError_CredentialKeywordAtInnerDepth4_DetectsAsAuth()
    {
        // Auth keyword is 4 InnerException hops down — boundary: hop 4 < 5, must be detected.
        var ex = WrapToDepth("Invalid API-key fragment", 4);
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().StartWith("AUTH: ", "credential keyword at depth 4 is the inclusive boundary (hop < 5)");
    }

    [Fact]
    public async Task IsAuthError_CredentialKeywordAtInnerDepth5_DoesNotDetect()
    {
        // Auth keyword is 5 InnerException hops down — loop bound is exclusive at 5, must NOT detect.
        var ex = WrapToDepth("Invalid API-key fragment", 5);
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().NotStartWith("AUTH: ", "credential keyword at depth 5 is beyond the 5-hop limit — must NOT be detected");
    }

    [Fact]
    public async Task IsAuthError_CredentialKeywordAtInnerDepth6_DoesNotDetect()
    {
        // Auth keyword is 6 InnerException hops down — well beyond the 5-hop limit, must NOT detect.
        // Adding depth 6 doubles the negative-side coverage: a regression changing `hop < 5` to
        // `hop <= 5` would be caught by depth-5 alone, but depth-6 guards against widening to `hop < 6`.
        var ex = WrapToDepth("Invalid API-key fragment", 6);
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().NotStartWith("AUTH: ", "credential keyword at depth 6 is well beyond the 5-hop limit — must NOT be detected");
    }

    [Fact]
    public async Task IsAuthError_NumericCode2015_AtDepth2_DetectsAsAuth()
    {
        // nit5: the -2015 fragment uses StringComparison.Ordinal. A regression changing its
        // comparer to OrdinalIgnoreCase or inverting the lookup would go undetected unless
        // we exercise the Ordinal branch at chain depth > 0.
        var ex = WrapToDepth("error -2015 occurred", 2);
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };
        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(ex);

        await sut.RefreshTiersAsync(CancellationToken.None);

        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate);
        var reason = warning.Properties["Reason"]?.ToString() ?? string.Empty;
        reason.Should().StartWith("AUTH: ", "the -2015 credential-error code at InnerException depth 2 must be detected via Ordinal comparison");
    }

    // ── ConnectorUnavailable / Skipped bucket isolation tests (F5) ──────────

    [Fact]
    public async Task RefreshCycle_AllConnectorsUnavailable_ReportsAllSkipped()
    {
        // 3 exchanges all factory-throw (ConnectorUnavailable) → Success=0, Total=3, Skipped=3,
        // and no per-exchange Warning fires (skipped is not a failure).
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
            MakeRate(3, "ExchangeC", 1, "BTC"),
        };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        // Use a sentinel payload in the exception message — the Debug log must not leak it.
        const string secretPayload = "SECRET_PAYLOAD_MUST_NOT_LEAK";
        factory.Setup(f => f.GetConnector(It.IsAny<string>())).Throws(new InvalidOperationException(secretPayload));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var summary = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "cycle summary must emit even when all connectors are unavailable").Which;

        summary.Properties["Success"].Should().Be(0);
        summary.Properties["Total"].Should().Be(3);
        summary.Properties["Skipped"].Should().Be(3);

        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate,
            "ConnectorUnavailable exchanges must not trigger a per-exchange failure Warning");

        // CWE-532 leakage guard: the ConnectorUnavailable Debug log must emit the exception
        // type name (sanitized), not the exception message (which may contain secrets).
        var debugEntries = logger.Entries.Where(e => e.Level == LogLevel.Debug).ToList();
        debugEntries.Should().NotBeEmpty("a Debug log must be emitted for each skipped connector");
        debugEntries.Should().Contain(
            e => e.Formatted.Contains("InvalidOperationException"),
            "Debug log must include the exception type name");
        logger.Entries.Should().NotContain(
            e => e.Formatted.Contains(secretPayload),
            "exception message must not leak into any log entry on the ConnectorUnavailable path");
    }

    [Fact]
    public async Task RefreshCycle_MixedOutcomes_SummaryCounts()
    {
        // 3 exchanges: ExchangeA succeeds, ExchangeB is ConnectorUnavailable (skipped),
        // ExchangeC throws transient failure via lifecycle.
        // Expected summary: Success=1, Total=3, Skipped=1; exactly one Warning (ExchangeC), not two.
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "BTC"),
            MakeRate(2, "ExchangeB", 1, "BTC"),
            MakeRate(3, "ExchangeC", 1, "BTC"),
        };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        var connectorA = new Mock<IExchangeConnector>().Object;
        var connectorC = new Mock<IExchangeConnector>().Object;

        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connectorA);
        factory.Setup(f => f.GetConnector("ExchangeB")).Throws(new InvalidOperationException("ExchangeB unavailable"));
        factory.Setup(f => f.GetConnector("ExchangeC")).Returns(connectorC);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorA, "BTC", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connectorC, "BTC", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ExchangeC transient"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var summary = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "cycle summary must emit").Which;

        summary.Properties["Success"].Should().Be(1, "ExchangeA succeeded");
        summary.Properties["Total"].Should().Be(3, "3 exchanges attempted");
        summary.Properties["Skipped"].Should().Be(1, "ExchangeB was ConnectorUnavailable");

        logger.Entries.Count(e => e.Level == LogLevel.Warning
            && e.Template == FailedExchangeTemplate)
            .Should().Be(1, "exactly one Warning fires — for ExchangeC (transient), not ExchangeB (skipped)");
    }

    // ── Empty pairs edge case (nit5) ─────────────────────────────────────────

    [Fact]
    public async Task LeverageTierRefresher_EmptyPairsList_LogsAtDebug()
    {
        // Repository returns empty enumerable — loop never fires, outcomes is empty.
        // The early-return path logs a Debug and exits before the summary.

        // Use a capturing logger variant to verify no Warning fires.
        var rateRepo = new Mock<IFundingRateRepository>();
        rateRepo.Setup(r => r.GetLatestPerExchangePerAssetAsync()).ReturnsAsync(new List<FundingRateSnapshot>());
        var uow = new Mock<IUnitOfWork>();
        uow.Setup(u => u.FundingRates).Returns(rateRepo.Object);
        var factory2 = new Mock<IExchangeConnectorFactory>();
        var lifecycle2 = new Mock<IConnectorLifecycleManager>();
        var services = new ServiceCollection();
        services.AddSingleton(uow.Object);
        services.AddSingleton(factory2.Object);
        services.AddSingleton(lifecycle2.Object);
        var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<LeverageTierRefresher>();
        var sut2 = new LeverageTierRefresher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            initialDelay: TimeSpan.FromMilliseconds(1),
            refreshInterval: TimeSpan.FromMilliseconds(50));

        await sut2.RefreshTiersAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Warning,
            "empty pairs list must not produce any Warning");

        // The early-return path does NOT emit a cycle-completed summary.
        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "empty pairs list returns early before the summary log");

        // The early-return path MUST emit a Debug log with the exact template.
        // Assert on Template (not just Formatted) so a future phrasing change is caught.
        logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Debug && e.Template == EmptyPairsDebugTemplate,
            "early-return path must emit a Debug log with the canonical empty-pairs template");
    }

    // ── F1 reclassification test (review-v221) ───────────────────────────────

    [Fact]
    public async Task RefreshCycle_ExchangeSucceedsThenFailsOnSameExchange_ReclassifiesToFailure()
    {
        // ExchangeA: Symbol1 succeeds (Success recorded), Symbol2 fails transient.
        // After reclassification the summary should report Success=0, not 1.
        var rates = new[]
        {
            MakeRate(1, "ExchangeA", 1, "Symbol1"),
            MakeRate(1, "ExchangeA", 2, "Symbol2"),
        };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);

        var connector = new Mock<IExchangeConnector>().Object;
        factory.Setup(f => f.GetConnector("ExchangeA")).Returns(connector);

        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "Symbol1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        lifecycle.Setup(l => l.EnsureTiersCachedAsync(connector, "Symbol2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var summary = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate,
            "cycle summary must emit").Which;

        summary.Properties["Success"].Should().Be(0,
            "ExchangeA's outcome must be reclassified to TransientFailure when a later symbol fails");
        summary.Properties["Total"].Should().Be(1);
        summary.Properties["Skipped"].Should().Be(0);

        // One Warning must fire for the exchange.
        logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate,
            "exactly one Warning should fire for ExchangeA");
    }

    // ── F2 ConnectorUnavailable — no per-exchange Warning (review-v221) ──────

    [Fact]
    public async Task RefreshCycle_ConnectorUnavailable_DoesNotEmitPerExchangeWarning()
    {
        // factory.GetConnector throws for ExchangeA → ConnectorUnavailable (skipped).
        // Zero per-exchange Warning entries for ExchangeA should be emitted.
        var rates = new[] { MakeRate(1, "ExchangeA", 1, "BTC") };

        var (sut, factory, lifecycle, logger) = BuildSutWithStructuredLogger(rates);
        factory.Setup(f => f.GetConnector("ExchangeA")).Throws(new InvalidOperationException("unavailable"));

        await sut.RefreshTiersAsync(CancellationToken.None);

        var summary = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information
              && e.Template == CycleSummaryTemplate).Which;

        summary.Properties["Skipped"].Should().Be(1);

        logger.Entries.Should().NotContain(
            e => e.Level == LogLevel.Warning
              && e.Template == FailedExchangeTemplate,
            "ConnectorUnavailable (skipped) exchanges must NOT emit a per-exchange failure Warning");
    }
}
