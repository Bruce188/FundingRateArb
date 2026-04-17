using System.Collections.Generic;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FundingRateArb.Tests.Integration.Logging;

/// <summary>
/// Validates that Program.cs's Serilog Filter.ByExcluding predicate correctly
/// suppresses the high-volume CryptoExchange/Binance.Net DateTime-null Warning while
/// allowing all other CryptoExchange/Binance.Net log events and the Binance.Net Error
/// minimum-level override to pass through unchanged.
/// </summary>
public class SerilogConfigurationTests
{
    private const string TargetMessage =
        "DateTime value of null, but property is not nullable. Resolver: BinanceSourceGenerationContext";

    /// <summary>
    /// Hand-rolled in-memory sink — avoids a NuGet dependency on Serilog.Sinks.InMemory.
    /// </summary>
    private sealed class InMemorySink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger logger, InMemorySink sink) BuildTestLogger()
    {
        var sink = new InMemorySink();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            // Mirror PR #184: Binance.Net minimum level raised to Error.
            .MinimumLevel.Override("Binance.Net", LogEventLevel.Error)
            // Mirror the hardened filter from Task 1.1: covers descendant SourceContexts
            // for both CryptoExchange and Binance.Net, and uses Contains for message matching.
            .Filter.ByExcluding(le =>
                le.Level == LogEventLevel.Warning &&
                le.Properties.TryGetValue("SourceContext", out var scValue) &&
                scValue is ScalarValue scScalar &&
                scScalar.Value is string scStr &&
                (scStr.StartsWith("CryptoExchange", StringComparison.Ordinal) ||
                 scStr.StartsWith("Binance.Net", StringComparison.Ordinal)) &&
                le.MessageTemplate.Text.Contains("DateTime value of null", StringComparison.Ordinal))
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (logger, sink);
    }

    // -------------------------------------------------------------------------
    // Existing cases from PR 8f746ce
    // -------------------------------------------------------------------------

    [Fact]
    public void CryptoExchange_DateTimeNullWarning_IsExcluded()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange")
            .Warning(TargetMessage);

        sink.Events.Should().BeEmpty(
            "the exact-match CryptoExchange DateTime-null Warning must be filtered out");
    }

    [Fact]
    public void CryptoExchange_OtherWarning_IsKept()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange")
            .Warning("Unexpected response from server");

        sink.Events.Should().ContainSingle(
            "a CryptoExchange Warning with a different message must pass through");
    }

    [Fact]
    public void CryptoExchange_Error_IsKept()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange")
            .Error(TargetMessage);

        sink.Events.Should().ContainSingle(
            "a CryptoExchange Error must pass through even when the message matches the filter (level guard)");
    }

    [Fact]
    public void BinanceNet_Error_IsKept()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "Binance.Net.Clients.UsdFuturesApi")
            .Error("Some Binance.Net error");

        sink.Events.Should().ContainSingle(
            "PR #184 MinimumLevel.Override raises Binance.Net minimum to Error, so Errors must still flow");
    }

    // -------------------------------------------------------------------------
    // New cases — Task 1.2
    // -------------------------------------------------------------------------

    [Fact]
    public void Filter_DropsDescendantCryptoExchangeSourceContext_WithExactMessage()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange.Net.SocketClient")
            .Warning(TargetMessage);

        sink.Events.Should().BeEmpty(
            "a descendant CryptoExchange.Net SourceContext with the matching message must be filtered");
    }

    [Fact]
    public void Filter_DropsBinanceNetDescendantSourceContext_WithMessageVariant()
    {
        var (logger, sink) = BuildTestLogger();

        // Use MinimumLevel.Debug so the Warning is not blocked by the Binance.Net
        // minimum-level override before reaching the Filter.ByExcluding predicate.
        // Build a separate logger without the MinimumLevel.Override for this case.
        var sinkVariant = new InMemorySink();
        var loggerVariant = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Filter.ByExcluding(le =>
                le.Level == LogEventLevel.Warning &&
                le.Properties.TryGetValue("SourceContext", out var scValue) &&
                scValue is ScalarValue scScalar &&
                scScalar.Value is string scStr &&
                (scStr.StartsWith("CryptoExchange", StringComparison.Ordinal) ||
                 scStr.StartsWith("Binance.Net", StringComparison.Ordinal)) &&
                le.MessageTemplate.Text.Contains("DateTime value of null", StringComparison.Ordinal))
            .WriteTo.Sink(sinkVariant)
            .CreateLogger();

        loggerVariant
            .ForContext("SourceContext",
                "Binance.Net.Clients.UsdFuturesApi.BinanceRestClientUsdFuturesApiExchangeData")
            .Warning(
                "DateTime value of null, but property is not nullable. Resolver: BinanceSourceGenerationContext (variant suffix)");

        sinkVariant.Events.Should().BeEmpty(
            "a Binance.Net descendant SourceContext with a message-variant still containing 'DateTime value of null' must be filtered");
    }

    [Fact]
    public void Filter_PreservesUnrelatedWarningFromCryptoExchangeSource()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange.Net.SocketClient")
            .Warning("Socket disconnected");

        sink.Events.Should().ContainSingle(
            "a legitimate CryptoExchange.Net Warning with an unrelated message must not be dropped");
    }

    [Fact]
    public void Filter_PreservesErrorEvenWithMatchingMessageAndSource()
    {
        var (logger, sink) = BuildTestLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange.Net.SocketClient")
            .Error(TargetMessage);

        sink.Events.Should().ContainSingle(
            "an Error must never be filtered regardless of message content — Warning gate must stand");
    }

    [Fact]
    public void Filter_IsAppliedOnOuterLoggerConfiguration_NoSubLoggerIsolation()
    {
        // Regression guard: attach two in-memory sinks directly to the outer LoggerConfiguration
        // (simulating WriteTo.Console + WriteTo.ApplicationInsights both on the outer lc).
        // Both must observe zero events when the matching Warning is emitted — the filter on
        // the outer config is inherited by all sinks, not bypassed by sub-logger wrapping.
        var sink1 = new InMemorySink();
        var sink2 = new InMemorySink();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Filter.ByExcluding(le =>
                le.Level == LogEventLevel.Warning &&
                le.Properties.TryGetValue("SourceContext", out var scValue) &&
                scValue is ScalarValue scScalar &&
                scScalar.Value is string scStr &&
                (scStr.StartsWith("CryptoExchange", StringComparison.Ordinal) ||
                 scStr.StartsWith("Binance.Net", StringComparison.Ordinal)) &&
                le.MessageTemplate.Text.Contains("DateTime value of null", StringComparison.Ordinal))
            .WriteTo.Sink(sink1)   // simulates Console
            .WriteTo.Sink(sink2)   // simulates ApplicationInsights
            .CreateLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange.Net.SocketClient")
            .Warning(TargetMessage);

        sink1.Events.Should().BeEmpty(
            "first outer-config sink (Console simulation) must not receive the filtered Warning");
        sink2.Events.Should().BeEmpty(
            "second outer-config sink (ApplicationInsights simulation) must not receive the filtered Warning");
    }
}
