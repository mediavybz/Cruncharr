using System.Collections.Concurrent;

namespace Cruncharr.API.Diagnostics;

/// <summary>
/// A single captured log entry.
/// </summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
}

/// <summary>
/// Bounded in-memory ring buffer of recent log entries. Lets the diagnostics API
/// surface what the app is doing (especially download failures) without needing
/// shell access to the container's stdout.
/// </summary>
public sealed class InMemoryLogStore
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly int _capacity;

    public InMemoryLogStore(int capacity = 2000)
    {
        _capacity = capacity;
    }

    public void Add(LogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Most recent entries first, optionally filtered by minimum level / category / text.
    /// </summary>
    public IReadOnlyList<LogEntry> Query(string? minLevel = null, string? category = null, string? contains = null, int limit = 200)
    {
        IEnumerable<LogEntry> q = _entries;

        if (!string.IsNullOrWhiteSpace(minLevel) && TryRank(minLevel, out var min))
        {
            q = q.Where(e => TryRank(e.Level, out var r) && r >= min);
        }
        if (!string.IsNullOrWhiteSpace(category))
        {
            q = q.Where(e => e.Category.Contains(category, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(contains))
        {
            q = q.Where(e => e.Message.Contains(contains, StringComparison.OrdinalIgnoreCase)
                          || (e.Exception?.Contains(contains, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (limit <= 0) limit = 200;
        return q.Reverse().Take(limit).ToList();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _))
        {
        }
    }

    private static bool TryRank(string level, out int rank)
    {
        rank = level switch
        {
            "Trace" => 0,
            "Debug" => 1,
            "Information" => 2,
            "Warning" => 3,
            "Error" => 4,
            "Critical" => 5,
            _ => -1
        };
        return rank >= 0;
    }
}

/// <summary>
/// ILoggerProvider that tees log entries into the in-memory ring buffer.
/// </summary>
public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly InMemoryLogStore _store;

    public InMemoryLoggerProvider(InMemoryLogStore store)
    {
        _store = store;
    }

    public ILogger CreateLogger(string categoryName) => new InMemoryLogger(categoryName, _store);

    public void Dispose()
    {
    }

    private sealed class InMemoryLogger : ILogger
    {
        private readonly string _category;
        private readonly InMemoryLogStore _store;

        public InMemoryLogger(string category, InMemoryLogStore store)
        {
            _category = category;
            _store = store;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            // Keep the buffer focused on application logs: drop chatty framework
            // Info/Debug (request routing, action invocation) but keep their warnings+.
            if (logLevel < LogLevel.Warning &&
                (_category.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                 _category.StartsWith("System.", StringComparison.Ordinal)))
            {
                return;
            }

            _store.Add(new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = logLevel.ToString(),
                Category = _category,
                Message = formatter(state, exception),
                Exception = exception?.ToString()
            });
        }
    }
}
