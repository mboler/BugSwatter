using System.Collections.Concurrent;

namespace Marshal;

/// <summary>Per-repository exponential backoff for transient endpoint outages. Each consecutive miss doubles the delay up to a cap; a success resets it. The cap keeps a long outage to a slow poll rather than a tight loop, and coalescing in the queue means the retries never pile up</summary>
public sealed class BackoffTracker
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;

    /// <summary>Creates a tracker with the base delay for the first retry and the cap the delay never exceeds</summary>
    public BackoffTracker(TimeSpan baseDelay, TimeSpan maxDelay)
    {
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
    }

    /// <summary>Records another consecutive miss for the key and returns the delay to wait before retrying</summary>
    public TimeSpan NextDelay(string key)
    {
        int attempt = _attempts.AddOrUpdate(key, 1, (_, current) => current + 1);

        // delay = base * 2^(attempt-1), capped; computed in ticks to avoid overflow at high attempt counts
        double factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        double ticks = Math.Min(_baseDelay.Ticks * factor, _maxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    /// <summary>Clears the backoff for a key after a reachable endpoint or a completed run</summary>
    public void Reset(string key) => _attempts.TryRemove(key, out _);

    /// <summary>Current consecutive-miss count for a key, for logging and tests</summary>
    public int AttemptCount(string key) => _attempts.TryGetValue(key, out int value) ? value : 0;
}
