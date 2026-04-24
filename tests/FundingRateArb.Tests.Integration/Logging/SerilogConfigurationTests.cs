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

    // review-v216: NB-1 — includeBinanceNetOverride=false lets the Warning reach the filter
    // under test without being blocked first by MinimumLevel.Override("Binance.Net", Error).
    private static (ILogger logger, InMemorySink sink) BuildTestLogger(
        bool includeBinanceNetOverride = true)
    {
        var sink = new InMemorySink();

        var config = new LoggerConfiguration()
            .MinimumLevel.Debug();

        if (includeBinanceNetOverride)
        {
            // Mirror PR #184: Binance.Net minimum level raised to Error.
            config = config.MinimumLevel.Override("Binance.Net", LogEventLevel.Error);
        }

        var logger = config
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
        // review-v216: NB-1 — omit MinimumLevel.Override("Binance.Net", Error) so the Warning
        // is not blocked before reaching Filter.ByExcluding; the shared predicate is exercised.
        var (loggerVariant, sinkVariant) = BuildTestLogger(includeBinanceNetOverride: false);

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
    public void Filter_SuppressesBeforeSubLogger_OnOuterLoggerConfiguration()
    {
        // review-v216: NB-2 — exercise the real sub-logger isolation risk: Serilog's
        // WriteTo.Logger(sub => ...) nests a sub-pipeline under the outer config.
        // Filters on the outer config run before events reach the sub-logger branch, so
        // the targeted Warning must be absent from BOTH the outer sink and the inner sink.
        var outerSink = new InMemorySink();
        var innerSink = new InMemorySink();

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
            .WriteTo.Sink(outerSink)
            .WriteTo.Logger(sub => sub.WriteTo.Sink(innerSink))
            .CreateLogger();

        logger
            .ForContext("SourceContext", "CryptoExchange.Net.SocketClient")
            .Warning(TargetMessage);

        outerSink.Events.Should().BeEmpty(
            "the outer-config filter must suppress the targeted Warning before it reaches the outer sink");
        innerSink.Events.Should().BeEmpty(
            "the outer-config filter must also suppress before the sub-logger branch; the inner sink must receive nothing");
    }
}
