using System.Collections.Concurrent;

namespace Cruncharr.API.Security;

/// <summary>
/// Counts failed attempts per client within a fixed window so a brute-force run against the
/// API key is cut off after a handful of guesses instead of being allowed at line rate.
/// </summary>
public sealed class FailureThrottle(int limit, TimeSpan window, Func<DateTime>? clock = null)
{
    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> _failures = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public bool IsBlocked(string client)
    {
        if (!_failures.TryGetValue(client, out var entry)) return false;
        if (_clock() - entry.WindowStart >= window)
        {
            _failures.TryRemove(client, out _);
            return false;
        }
        return entry.Count >= limit;
    }

    public void RecordFailure(string client)
    {
        var now = _clock();
        _failures.AddOrUpdate(client, _ => (now, 1),
            (_, entry) => now - entry.WindowStart >= window ? (now, 1) : (entry.WindowStart, entry.Count + 1));
        if (_failures.Count > 10_000)
        {
            foreach (var pair in _failures)
                if (now - pair.Value.WindowStart >= window) _failures.TryRemove(pair.Key, out _);
        }
    }

    public void Reset(string client) => _failures.TryRemove(client, out _);
}
