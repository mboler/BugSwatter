namespace Informant;

/// <summary>Tracks one optional pass-level wall-clock budget and creates request tokens bounded by its deadline</summary>
public sealed class ReviewTimeBudget
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a budget at the provider's current UTC time</summary>
    public ReviewTimeBudget(int? configuredMinutes, TimeProvider? timeProvider = null)
    {
        if (configuredMinutes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredMinutes), configuredMinutes, "The configured review budget must be greater than zero.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        ConfiguredMinutes = configuredMinutes;
        StartedAtUtc = _timeProvider.GetUtcNow();
        DeadlineUtc = configuredMinutes is null ? null : StartedAtUtc.AddMinutes(configuredMinutes.Value);
    }

    /// <summary>Configured pass limit in minutes, or null for an unbounded pass</summary>
    public int? ConfiguredMinutes { get; }

    /// <summary>UTC instant at which budget accounting began</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>UTC deadline, or null for an unbounded pass</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>Whether the configured deadline has been reached</summary>
    public bool IsExhausted => DeadlineUtc is not null && _timeProvider.GetUtcNow() >= DeadlineUtc.Value;

    /// <summary>Remaining wall-clock time, or null for an unbounded pass</summary>
    public TimeSpan? Remaining => DeadlineUtc is null ? null : Max(TimeSpan.Zero, DeadlineUtc.Value - _timeProvider.GetUtcNow());

    /// <summary>Creates a linked token source that cancels no later than the remaining budget</summary>
    public CancellationTokenSource CreateLinkedTokenSource(CancellationToken cancellationToken = default)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Remaining is TimeSpan remaining)
        {
            source.CancelAfter(remaining);
        }

        return source;
    }

    /// <summary>Whether cancellation came from this budget rather than the caller</summary>
    public bool CausedCancellation(CancellationToken cancellationToken) => IsExhausted && !cancellationToken.IsCancellationRequested;

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}
