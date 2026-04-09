using Serilog;

namespace FundingRateArb.Web.Infrastructure;

/// <summary>
/// Static handlers registered against <see cref="AppDomain.CurrentDomain.UnhandledException"/>
/// and <see cref="TaskScheduler.UnobservedTaskException"/> so any managed exception that
/// escapes the normal error-handling pipeline is captured in Serilog at Fatal level before
/// the process tears down. These handlers cannot prevent native crashes (SIGSEGV etc.), but
/// they surface managed failures that would otherwise be invisible in Application Insights.
/// </summary>
/// <remarks>
/// Keep the handler bodies tiny — no allocation beyond the log line, no awaits, no
/// <see cref="Environment.Exit"/>. They run on arbitrary threads during shutdown.
/// </remarks>
public static class UnhandledExceptionHandlers
{
    /// <summary>
    /// Handler for <see cref="AppDomain.UnhandledException"/>. Logs the exception at
    /// Fatal level via Serilog. Defensive against non-<see cref="Exception"/> ExceptionObject
    /// values (the runtime technically permits a non-Exception payload).
    /// </summary>
    public static void OnAppDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(
            ex,
            "{HandlerName} handler invoked (terminating={IsTerminating}): {Message}",
            "AppDomain.UnhandledException",
            e.IsTerminating,
            ex?.Message ?? e.ExceptionObject?.ToString() ?? "<null>");
    }

    /// <summary>
    /// Handler for <see cref="TaskScheduler.UnobservedTaskException"/>. Logs the
    /// aggregate exception at Fatal level and marks it as observed so the default
    /// runtime behaviour (process crash on .NET Framework / warning on .NET 5+) does
    /// not trigger a secondary unhandled flow.
    /// </summary>
    public static void OnTaskUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Fatal(
            e.Exception,
            "{HandlerName} handler invoked: {Message}",
            "TaskScheduler.UnobservedTaskException",
            e.Exception?.Message);

        e.SetObserved();
    }
}
