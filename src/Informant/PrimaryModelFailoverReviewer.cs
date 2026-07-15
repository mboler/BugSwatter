using Serilog;

namespace Informant;

/// <summary>One verified model-specific review session used by the primary failover coordinator</summary>
public interface IPrimaryModelSession
{
    /// <summary>Configured model represented by this session</summary>
    PrimaryModelTarget Target { get; }

    /// <summary>Proves that the target performs the required read-only tool call</summary>
    Task<VerificationResult> VerifyAsync(CancellationToken cancellationToken = default);

    /// <summary>Reviews one changed file with this target</summary>
    Task<FileReviewResult> ReviewAsync(ChangedFile file, CancellationToken cancellationToken = default);

    /// <summary>Requests one bounded tool-free repository plan from this target</summary>
    Task<string> PlanAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    /// <summary>Reviews one complete clustered execution unit with this target</summary>
    Task<ReviewUnitResult> ReviewUnitAsync(ReviewExecutionUnit unit, CancellationToken cancellationToken = default);
}

/// <summary>Production primary-model session backed by one model client and one file reviewer</summary>
public sealed class PrimaryModelSession : IPrimaryModelSession
{
    private readonly ModelClient _client;
    private readonly FileReviewer _reviewer;
    private readonly ClusteredReviewReviewer _clusteredReviewer;
    private readonly int _maxContextCharacters;

    /// <summary>Creates a session over an already-running configured model</summary>
    public PrimaryModelSession(PrimaryModelTarget target, ModelClient client, FileReviewer reviewer, ClusteredReviewReviewer clusteredReviewer, int maxContextCharacters)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(clusteredReviewer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContextCharacters);

        Target = target;
        _client = client;
        _reviewer = reviewer;
        _clusteredReviewer = clusteredReviewer;
        _maxContextCharacters = maxContextCharacters;
    }

    /// <inheritdoc />
    public PrimaryModelTarget Target { get; }

    /// <inheritdoc />
    public Task<VerificationResult> VerifyAsync(CancellationToken cancellationToken = default) => ToolCallingVerifier.VerifyAsync(_client, _maxContextCharacters, cancellationToken);

    /// <inheritdoc />
    public Task<FileReviewResult> ReviewAsync(ChangedFile file, CancellationToken cancellationToken = default) => _reviewer.ReviewAsync(file, cancellationToken);

    /// <inheritdoc />
    public async Task<string> PlanAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        ChatMessage response = await _client.CompleteAsync([new ChatMessage { Role = "system", Content = systemPrompt }, new ChatMessage { Role = "user", Content = userPrompt }], [], cancellationToken);
        return !string.IsNullOrWhiteSpace(response.Content) ? response.Content : throw new ModelCallException("Model returned an empty repository planning answer");
    }

    /// <inheritdoc />
    public Task<ReviewUnitResult> ReviewUnitAsync(ReviewExecutionUnit unit, CancellationToken cancellationToken = default) => _clusteredReviewer.ReviewAsync(unit, cancellationToken);
}

/// <summary>A model target that failed verification or exhausted its retries during a primary review</summary>
public sealed record PrimaryModelFailure(PrimaryModelTarget Target, string Reason);

/// <summary>Uses one primary model at a time and permanently advances to the next configured session after a model-layer failure</summary>
public sealed class PrimaryModelFailoverReviewer
{
    private readonly IReadOnlyList<IPrimaryModelSession> _sessions;
    private readonly List<PrimaryModelFailure> _failures = [];
    private readonly Action<PrimaryModelTarget>? _targetObserver;

    private IPrimaryModelSession? _activeSession;
    private int _nextSessionIndex;

    /// <summary>Creates the ordered failover coordinator</summary>
    public PrimaryModelFailoverReviewer(IReadOnlyList<IPrimaryModelSession> sessions, Action<PrimaryModelTarget>? targetObserver = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            throw new ArgumentException("At least one primary model session is required.", nameof(sessions));
        }

        if (sessions.Any(session => session is null))
        {
            throw new ArgumentException("Primary model sessions must not contain null.", nameof(sessions));
        }

        _sessions = sessions;
        _targetObserver = targetObserver;
    }

    /// <summary>The currently selected model, or null before initialization and after every target has failed</summary>
    public PrimaryModelTarget? ActiveTarget => _activeSession?.Target;

    /// <summary>Target failures observed so far, in attempt order</summary>
    public IReadOnlyList<PrimaryModelFailure> Failures => _failures;

    /// <summary>Selects and verifies the first usable target, throwing when none perform the required tool call</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (await ActivateNextAsync(cancellationToken))
        {
            return;
        }

        string details = string.Join("; ", _failures.Select(failure => $"{failure.Target.Name}: {failure.Reason}"));
        throw new InformantFatalException($"No configured primary model passed tool-calling verification. {details}");
    }

    /// <summary>Reviews one file, retrying the whole file on the next verified model only after a model-layer failure</summary>
    public async Task<FileReviewResult> ReviewAsync(ChangedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (_activeSession is null)
        {
            return new FileReviewResult(file, FileReviewStatus.Failed, null, 0, 0, "all configured primary models were exhausted earlier in the run", FailureKind: FileReviewFailureKind.Model);
        }

        while (_activeSession is { } session)
        {
            FileReviewResult result = AttachModel(await session.ReviewAsync(file, cancellationToken), session.Target);
            if (result.FailureKind != FileReviewFailureKind.Model)
            {
                return result;
            }

            RecordFailure(session.Target, result.SkipReason ?? "model review failed without a reason");
            _activeSession = null;
            if (!await ActivateNextAsync(cancellationToken))
            {
                return result;
            }

            Log.Warning("Retrying {Path} from the beginning with fallback model target {Target}", file.Path, _activeSession!.Target.Name);
        }

        throw new InvalidOperationException("The primary model failover loop ended without a result.");
    }

    /// <summary>Requests a planning batch and permanently advances to a fallback target after a model-layer failure</summary>
    public async Task<string> PlanAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPrompt);
        if (_activeSession is null && _nextSessionIndex == 0)
        {
            throw new InvalidOperationException("InitializeAsync must complete before repository planning can begin.");
        }

        while (_activeSession is { } session)
        {
            try
            {
                return await session.PlanAsync(systemPrompt, userPrompt, cancellationToken);
            }
            catch (ModelCallException ex)
            {
                RecordFailure(session.Target, $"repository planning failed: {ex.Message}");
                _activeSession = null;
                if (!await ActivateNextAsync(cancellationToken))
                {
                    break;
                }

                Log.Warning("Retrying repository planning batch from the beginning with fallback model target {Target}", _activeSession!.Target.Name);
            }
        }

        string details = string.Join("; ", _failures.Select(failure => $"{failure.Target.Name}: {failure.Reason}"));
        throw new InformantFatalException($"All configured primary models were exhausted during repository planning. {details}");
    }

    /// <summary>Reviews one clustered unit, restarting that complete unit on the next verified model after a model-layer failure</summary>
    public async Task<ReviewUnitResult> ReviewUnitAsync(ReviewExecutionUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (_activeSession is null)
        {
            return new ReviewUnitResult(unit, [], FileReviewFailureKind.Model, "all configured primary models were exhausted earlier in the run");
        }

        while (_activeSession is { } session)
        {
            ReviewUnitResult result = AttachModel(await session.ReviewUnitAsync(unit, cancellationToken), session.Target);
            if (result.FailureKind != FileReviewFailureKind.Model)
            {
                return result;
            }

            RecordFailure(session.Target, result.FailureReason ?? "clustered model review failed without a reason");
            _activeSession = null;
            if (!await ActivateNextAsync(cancellationToken))
            {
                return result;
            }

            Log.Warning("Retrying review unit {Unit} from the beginning with fallback model target {Target}", unit.Id, _activeSession!.Target.Name);
        }

        throw new InvalidOperationException("The primary model failover loop ended without a clustered result");
    }

    private async Task<bool> ActivateNextAsync(CancellationToken cancellationToken)
    {
        while (_nextSessionIndex < _sessions.Count)
        {
            IPrimaryModelSession candidate = _sessions[_nextSessionIndex++];
            ObserveTarget(candidate.Target);
            Log.Information("Verifying primary model target {Target}: {Model} at {Endpoint}", candidate.Target.Name, candidate.Target.ModelName, candidate.Target.Endpoint);

            VerificationResult verification = await candidate.VerifyAsync(cancellationToken);
            if (verification.Success)
            {
                _activeSession = candidate;
                Log.Information("Primary model target {Target} selected: {Model} at {Endpoint}", candidate.Target.Name, candidate.Target.ModelName, candidate.Target.Endpoint);
                return true;
            }

            RecordFailure(candidate.Target, $"tool-calling verification failed: {verification.Detail}");
        }

        return false;
    }

    private void RecordFailure(PrimaryModelTarget target, string reason)
    {
        _failures.Add(new PrimaryModelFailure(target, reason));
        Log.Warning("Primary model target {Target} is unavailable for the remainder of this run: {Reason}", target.Name, reason);
    }

    private void ObserveTarget(PrimaryModelTarget target)
    {
        try
        {
            _targetObserver?.Invoke(target);
        }
        catch (Exception ex)
        {
            // catch-all: optional progress telemetry must never change model selection or the review result
            Log.Warning("Primary model target observer failed: {Reason}", ex.Message);
        }
    }

    private static FileReviewResult AttachModel(FileReviewResult result, PrimaryModelTarget target)
    {
        if (result.Status == FileReviewStatus.NotReviewable || result.FailureKind == FileReviewFailureKind.Repository)
        {
            return result;
        }

        return result with { ReviewModelName = target.ModelName, ReviewModelProfile = target.Name };
    }

    private static ReviewUnitResult AttachModel(ReviewUnitResult result, PrimaryModelTarget target) =>
        result with { ReviewModelName = target.ModelName, ReviewModelProfile = target.Name };
}
