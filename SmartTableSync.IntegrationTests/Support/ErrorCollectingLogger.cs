using Microsoft.Extensions.Logging;

namespace SmartTableSync.IntegrationTests.Support;

/// <summary>Keeps every Error-level message. SyncOrchestrator logs a failed table and carries on
/// with the next one, so tests read the failures from here to report the actual SQL error
/// instead of only a wrong row count.</summary>
internal sealed class ErrorCollectingLogger<T> : ILogger<T>
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
            {
                return [.. _errors];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = exception is null ? formatter(state, exception) : $"{formatter(state, exception)}: {exception.Message}";
        lock (_errors)
        {
            _errors.Add(message);
        }
    }
}
