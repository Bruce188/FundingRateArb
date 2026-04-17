using System.Collections.Generic;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FundingRateArb.Tests.Integration.Logging;

/// <summary>
/// Validates that Program.cs's Serilog Filter.ByExcluding predicate correctly
/// suppresses the high-volume CryptoExchange DateTime-null Warning while allowing
/// all other CryptoExchange log events and the Binance.Net Error minimum-level
/// override to pass through unchanged.
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
            // Mirror the CryptoExchange exact-message filter added in Task 1.1.
            .Filter.ByExcluding(le =>
                le.Level == LogEventLevel.Warning &&
                le.Properties.TryGetValue("SourceContext", out var scValue) &&
                scValue is ScalarValue scScalar &&
                scScalar.Value is string scStr &&
                scStr.StartsWith("CryptoExchange", StringComparison.Ordinal) &&
                le.MessageTemplate.Text == TargetMessage)
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (logger, sink);
    }

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
}
