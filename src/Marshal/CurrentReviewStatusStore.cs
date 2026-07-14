using BugSwatter.Common;

namespace Marshal;

/// <summary>Current review details exposed by the status API; token counts come only from provider usage fields on completed model responses</summary>
public sealed record CurrentReviewActivity
{
    /// <summary>Configured job name</summary>
    public required string Job { get; init; }

    /// <summary>Trigger that enqueued the review</summary>
    public required string Trigger { get; init; }

    /// <summary>When Marshal dispatched the review</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>Latest reported phase</summary>
    public required string Phase { get; init; }

    /// <summary>Model selected for the phase, when applicable</summary>
    public string? ModelName { get; init; }

    /// <summary>Logical primary or second-opinion profile</summary>
    public string? ModelProfile { get; init; }

    /// <summary>Repository-relative file currently under review</summary>
    public string? CurrentFile { get; init; }

    /// <summary>One-based current file position</summary>
    public int? FileIndex { get; init; }

    /// <summary>File count in the current pass</summary>
    public int? FileCount { get; init; }

    /// <summary>True while Informant awaits a model response</summary>
    public bool ModelRequestActive { get; init; }

    /// <summary>When the active model request began, or null between requests</summary>
    public DateTimeOffset? ModelRequestStartedUtc { get; init; }

    /// <summary>Number of model requests started during the review</summary>
    public int ModelRequestCount { get; init; }

    /// <summary>Provider-reported prompt tokens across completed responses</summary>
    public long? PromptTokens { get; init; }

    /// <summary>Provider-reported completion tokens across completed responses</summary>
    public long? CompletionTokens { get; init; }

    /// <summary>Provider-reported total tokens across completed responses</summary>
    public long? TotalTokens { get; init; }
}

/// <summary>Thread-safe current-review store updated by the dispatcher and validated Informant progress lines</summary>
public sealed class CurrentReviewStatusStore
{
    private readonly Lock _gate = new();
    private CurrentReviewActivity? _activity;

    /// <summary>Starts a dispatched review with useful status before any valid Informant progress has arrived</summary>
    public void Begin(ReviewRequest request, DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _activity = new CurrentReviewActivity { Job = request.Job.Name, Trigger = request.Reason, StartedUtc = startedUtc, Phase = "Starting Informant" };
        }
    }

    /// <summary>Applies a complete validated Informant snapshot when it belongs to the current job, ignoring stale output from any other run</summary>
    public void Apply(string jobName, ReviewProgressSnapshot progress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(progress);

        lock (_gate)
        {
            if (_activity is null || !string.Equals(_activity.Job, jobName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activity = _activity with
            {
                Phase = progress.Phase,
                ModelName = progress.ModelName,
                ModelProfile = progress.ModelProfile,
                CurrentFile = progress.CurrentFile,
                FileIndex = progress.FileIndex,
                FileCount = progress.FileCount,
                ModelRequestActive = progress.ModelRequestActive,
                ModelRequestStartedUtc = progress.ModelRequestStartedUtc,
                ModelRequestCount = progress.ModelRequestCount,
                PromptTokens = progress.PromptTokens,
                CompletionTokens = progress.CompletionTokens,
                TotalTokens = progress.TotalTokens
            };
        }
    }

    /// <summary>Clears the current review only when the named job still owns the status slot</summary>
    public void Clear(string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        lock (_gate)
        {
            if (_activity is not null && string.Equals(_activity.Job, jobName, StringComparison.OrdinalIgnoreCase))
            {
                _activity = null;
            }
        }
    }

    /// <summary>Returns an immutable snapshot of the current review, or null when idle</summary>
    public CurrentReviewActivity? Snapshot()
    {
        lock (_gate)
        {
            return _activity;
        }
    }
}
