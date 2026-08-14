namespace Marshal;

/// <summary>Clock-aware scheduled waits that recheck wall time after timer wakeups and clock adjustments</summary>
internal static class ClockAwareDelay
{
    private static readonly TimeSpan MaximumClockCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Waits until the local wall clock reaches the target</summary>
    public static async Task UntilLocalAsync(TimeProvider timeProvider, DateTime targetLocal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        while (true)
        {
            TimeSpan remaining = targetLocal - timeProvider.GetLocalNow().DateTime;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(Minimum(remaining, MaximumClockCheckInterval), timeProvider, cancellationToken);
        }
    }

    /// <summary>Waits until the UTC wall clock reaches the target</summary>
    public static async Task UntilUtcAsync(TimeProvider timeProvider, DateTimeOffset targetUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        while (true)
        {
            TimeSpan remaining = targetUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(Minimum(remaining, MaximumClockCheckInterval), timeProvider, cancellationToken);
        }
    }

    private static TimeSpan Minimum(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
