using System.Threading.Channels;
using Serilog;

namespace Marshal;

/// <summary>What happened to an enqueue attempt</summary>
public enum EnqueueResult
{
    /// <summary>Added to the queue</summary>
    Enqueued,

    /// <summary>A request for the same repository is already waiting; the two coalesced into one</summary>
    CoalescedWithQueued,

    /// <summary>The repository is being reviewed right now; a single rerun was flagged for when it finishes</summary>
    RerunPending,

    /// <summary>The repository is being reviewed and a rerun is already flagged; the trigger collapsed into it</summary>
    RerunAlreadyPending,

    /// <summary>The queue is full; the request was dropped</summary>
    DroppedQueueFull
}

/// <summary>One queued review need: the job and the trigger that raised it</summary>
public sealed record ReviewRequest(ReviewJobConfig Job, string Reason);

/// <summary>The dispatch heart: a bounded channel of review requests with duplicate coalescing by repository identity, and a single pending-rerun flag for the repository currently under review. All state is in-memory and evaporates on restart by design. Every channel access happens under the lock, and the consumer waits with WaitToReadAsync then reads under the lock, so a snapshot or removal never races a take</summary>
public sealed class ReviewQueue
{
    /// <summary>Maximum number of waiting requests; overflow is dropped with a warning</summary>
    public const int MaxLength = 128;

    private static readonly StringComparer KeyComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _gate = new();

    private readonly Channel<ReviewRequest> _channel = Channel.CreateBounded<ReviewRequest>(new BoundedChannelOptions(MaxLength)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private readonly HashSet<string> _waitingKeys = new(KeyComparer);

    private string? _runningKey;
    private ReviewJobConfig? _runningJob;
    private bool _rerunPending;

    /// <summary>Number of requests currently waiting</summary>
    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waitingKeys.Count;
            }
        }
    }

    /// <summary>Name of the repository currently under review, or null when idle</summary>
    public string? RunningJobName
    {
        get
        {
            lock (_gate)
            {
                return _runningJob?.Name;
            }
        }
    }

    /// <summary>Repository identity for duplicate detection: the job's Informant config path, normalized, compared case-insensitively on Windows</summary>
    public static string RepositoryKey(ReviewJobConfig job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Path.GetFullPath(job.InformantConfigPath);
    }

    /// <summary>Returns whether the repository is already waiting or currently under review</summary>
    public bool IsQueuedOrRunning(ReviewJobConfig job)
    {
        ArgumentNullException.ThrowIfNull(job);
        string key = RepositoryKey(job);

        lock (_gate)
        {
            return _waitingKeys.Contains(key) || _runningKey is not null && KeyComparer.Equals(key, _runningKey);
        }
    }

    /// <summary>Enqueues a review need, coalescing duplicates and flagging a rerun when the repository is currently running</summary>
    /// <param name="job">The repository to review; its Informant config path is the identity used for coalescing</param>
    /// <param name="reason">Human-readable trigger description carried into the dispatch log</param>
    /// <returns>What happened to the request, already logged; tests assert on it and callers may ignore it</returns>
    public EnqueueResult Enqueue(ReviewJobConfig job, string reason)
    {
        ArgumentNullException.ThrowIfNull(job);
        string key = RepositoryKey(job);

        lock (_gate)
        {
            if (_runningKey is not null && KeyComparer.Equals(key, _runningKey))
            {
                if (_rerunPending)
                {
                    Log.Information("Trigger '{Reason}' for {Job}: a rerun is already pending; collapsed", reason, job.Name);
                    return EnqueueResult.RerunAlreadyPending;
                }

                _rerunPending = true;
                Log.Information("Trigger '{Reason}' for {Job}: review currently running; one rerun flagged for when it finishes", reason, job.Name);
                return EnqueueResult.RerunPending;
            }

            if (_waitingKeys.Contains(key))
            {
                Log.Information("Trigger '{Reason}' for {Job}: already waiting in the queue; coalesced", reason, job.Name);
                return EnqueueResult.CoalescedWithQueued;
            }

            // Non-blocking write: a full bounded channel drops the request rather than backpressuring a trigger
            if (!_channel.Writer.TryWrite(new ReviewRequest(job, reason)))
            {
                Log.Warning("Trigger '{Reason}' for {Job}: queue is full ({Max} entries); request dropped", reason, job.Name, MaxLength);
                return EnqueueResult.DroppedQueueFull;
            }

            _waitingKeys.Add(key);
            Log.Information("Trigger '{Reason}' for {Job}: enqueued ({Depth} waiting)", reason, job.Name, _waitingKeys.Count);

            return EnqueueResult.Enqueued;
        }
    }

    /// <summary>Waits for and removes the next request, marking its repository as running</summary>
    public async Task<ReviewRequest> TakeNextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // Wait for a signal outside the lock, then take the item under the lock so a concurrent snapshot or
            // removal that drains the channel cannot leave two readers fighting over the same entry
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The review queue channel was completed; the queue never completes by design");
            }

            lock (_gate)
            {
                if (_channel.Reader.TryRead(out ReviewRequest? request))
                {
                    string key = RepositoryKey(request.Job);
                    _waitingKeys.Remove(key);
                    _runningKey = key;
                    _runningJob = request.Job;
                    _rerunPending = false;
                    return request;
                }
            }
        }
    }

    /// <summary>Clears the running marker; when a rerun was flagged the repository is enqueued once more</summary>
    /// <returns>True when a flagged rerun was re-queued; false when none was flagged or the queue was full, the latter already logged as a warning by the enqueue</returns>
    public bool CompleteRunning()
    {
        ReviewJobConfig? rerunJob;

        lock (_gate)
        {
            rerunJob = _rerunPending ? _runningJob : null;
            _runningKey = null;
            _runningJob = null;
            _rerunPending = false;
        }

        if (rerunJob is null)
        {
            return false;
        }

        return Enqueue(rerunJob, "pending rerun after completed review") == EnqueueResult.Enqueued;
    }

    /// <summary>Snapshots the waiting requests, oldest first, for the status and queue views</summary>
    public IReadOnlyList<ReviewRequest> SnapshotWaiting()
    {
        lock (_gate)
        {
            List<ReviewRequest> items = DrainLocked();
            foreach (ReviewRequest item in items)
            {
                _channel.Writer.TryWrite(item);
            }

            return items;
        }
    }

    /// <summary>Removes the waiting request for the named job, if one is waiting; the review currently running is never touched</summary>
    /// <returns>True when a waiting request was removed, false when no matching job was waiting</returns>
    public bool RemoveWaiting(string jobName)
    {
        ArgumentNullException.ThrowIfNull(jobName);

        lock (_gate)
        {
            // A channel has no arbitrary removal, so drain it and re-push everything except the first matching request
            List<ReviewRequest> items = DrainLocked();
            bool removed = false;
            foreach (ReviewRequest item in items)
            {
                if (!removed && string.Equals(item.Job.Name, jobName, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    _waitingKeys.Remove(RepositoryKey(item.Job));
                    continue;
                }

                _channel.Writer.TryWrite(item);
            }

            if (removed)
            {
                Log.Information("Removed the waiting review for {Job}", jobName);
            }

            return removed;
        }
    }

    private List<ReviewRequest> DrainLocked()
    {
        var items = new List<ReviewRequest>();
        while (_channel.Reader.TryRead(out ReviewRequest? item))
        {
            items.Add(item);
        }

        return items;
    }
}
