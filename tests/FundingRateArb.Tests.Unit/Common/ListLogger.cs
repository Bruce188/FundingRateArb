using Microsoft.Extensions.Logging;

namespace FundingRateArb.Tests.Unit.Common;

/// <summary>
/// Minimal in-memory <see cref="ILogger{T}"/> that captures every formatted log entry into
/// a list so tests can assert on the rendered message text (including structured arguments).
/// Thread-safe for the single-writer scenarios used in connector / service tests.
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (Entries)
        {
            Entries.Add((logLevel, message, exception));
        }
    }

    public bool ContainsMessage(string substring, LogLevel? level = null)
    {
        lock (Entries)
        {
            return Entries.Any(e =>
                (level is null || e.Level == level) &&
                e.Message.Contains(substring, StringComparison.Ordinal));
        }
    }

    public int CountMessages(LogLevel level, string substring)
    {
        lock (Entries)
        {
            return Entries.Count(e => e.Level == level && e.Message.Contains(substring, StringComparison.Ordinal));
        }
    }
}
