using FluentAssertions;
using FundingRateArb.Web.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FundingRateArb.Tests.Unit;

/// <summary>
/// Tests for <see cref="UnhandledExceptionHandlers"/> — the static handlers wired into
/// <c>AppDomain.CurrentDomain.UnhandledException</c> and
/// <c>TaskScheduler.UnobservedTaskException</c> at application startup.
/// </summary>
public class ProgramUnhandledExceptionHandlerTests : IDisposable
{
    private readonly TestSink _sink;
    private readonly ILogger _previousLogger;

    public ProgramUnhandledExceptionHandlerTests()
    {
        _sink = new TestSink();
        _previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.Logger = _previousLogger;
    }

    [Fact]
    public void OnAppDomainUnhandled_Logs_FatalLevel_WithExceptionType()
    {
        var ex = new InvalidOperationException("boom");
        var args = new UnhandledExceptionEventArgs(ex, isTerminating: true);

        UnhandledExceptionHandlers.OnAppDomainUnhandled(sender: null, args);

        _sink.Events.Should().ContainSingle();
        var evt = _sink.Events.Single();
        evt.Level.Should().Be(LogEventLevel.Fatal);
        evt.Exception.Should().Be(ex);
    }

    [Fact]
    public void OnTaskUnobserved_Logs_FatalLevel_AndCallsSetObserved()
    {
        // Build an UnobservedTaskExceptionEventArgs from a real faulted task.
        var aggregate = new AggregateException(new InvalidOperationException("unobserved"));
        var args = new UnobservedTaskExceptionEventArgs(aggregate);

        UnhandledExceptionHandlers.OnTaskUnobserved(sender: null, args);

        _sink.Events.Should().ContainSingle(e => e.Level == LogEventLevel.Fatal);
        _sink.Events.Single().Exception.Should().Be(aggregate);

        // Verify SetObserved was called — the Observed property flips to true.
        args.Observed.Should().BeTrue();
    }

    [Fact]
    public void OnAppDomainUnhandled_NullExceptionObject_DoesNotThrow()
    {
        // Defensive: synthetic args whose ExceptionObject is not an Exception instance.
        var args = new UnhandledExceptionEventArgs("not-an-exception", isTerminating: true);

        var act = () => UnhandledExceptionHandlers.OnAppDomainUnhandled(sender: null, args);

        act.Should().NotThrow();
        _sink.Events.Should().ContainSingle(e => e.Level == LogEventLevel.Fatal);
    }

    /// <summary>
    /// Captures emitted log events for assertions.
    /// </summary>
    private sealed class TestSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }
}
