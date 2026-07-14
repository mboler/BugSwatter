using Microsoft.Extensions.Hosting;
using Serilog;

namespace Marshal;

/// <summary>The single serial executor: takes one request at a time from the queue, supervises the Informant child run, logs the outcome, and requeues once when a rerun was flagged while the review ran. At most one review runs at any moment because all reviews share one local model endpoint</summary>
public sealed class ReviewDispatcher : BackgroundService
{
    private readonly ReviewQueue _queue;
    private readonly IInformantRunner _runner;
    private readonly IEndpointHealthChecker _healthChecker;
    private readonly BackoffTracker _backoff;
    private readonly RunHistoryStore _history;
    private readonly CurrentReviewStatusStore _current;

    /// <summary>Creates the dispatcher over the shared queue, runner, endpoint health checker, backoff tracker and history store</summary>
    public ReviewDispatcher(ReviewQueue queue, IInformantRunner runner, IEndpointHealthChecker healthChecker, BackoffTracker backoff, RunHistoryStore history, CurrentReviewStatusStore current)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(healthChecker);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(current);

        _queue = queue;
        _runner = runner;
        _healthChecker = healthChecker;
        _backoff = backoff;
        _history = history;
        _current = current;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("Dispatcher started: serial execution, queue capacity {Capacity}", ReviewQueue.MaxLength);

        while (!stoppingToken.IsCancellationRequested)
        {
            ReviewRequest request;
            try
            {
                request = await _queue.TakeNextAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!await IsEndpointHealthyAsync(request, stoppingToken))
            {
                // Endpoint is down; the run marker was set by TakeNext, so clear it and let the scheduled retry re-enqueue
                _queue.CompleteRunning();
                continue;
            }

            Log.Information("Review starting for {Job} (trigger: {Reason})", request.Job.Name, request.Reason);
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            _current.Begin(request, startedUtc);
            bool cancelled = false;

            try
            {
                var outcome = await _runner.RunAsync(request.Job, stoppingToken);
                LogOutcome(request, outcome);
                RecordHistory(request, startedUtc, outcome);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                // catch-all: the runner reports expected failures as outcomes, so anything landing here is unexpected, and one broken run must never stop the dispatcher and strand the in-memory queue
                Log.Error(ex, "Review of {Job} aborted by an unexpected error; continuing with the next queued request", request.Job.Name);
                RecordHistory(request, startedUtc, null, ex.Message);
            }
            finally
            {
                _current.Clear(request.Job.Name);
                if (_queue.CompleteRunning())
                {
                    Log.Information("Rerun re-queued for {Job}: triggers arrived while its review ran", request.Job.Name);
                }
            }

            if (cancelled)
            {
                break;
            }
        }

        Log.Information("Dispatcher stopped");
    }

    /// <summary>Health-checks the job's preferred and fallback model endpoints; when none answer it schedules a backoff retry, while any reachable endpoint lets Informant perform the final model selection</summary>
    private async Task<bool> IsEndpointHealthyAsync(ReviewRequest request, CancellationToken stoppingToken)
    {
        string key = ReviewQueue.RepositoryKey(request.Job);
        IReadOnlyList<string> endpoints = JobConfigReader.TryReadModelEndpoints(request.Job.InformantConfigPath);

        // No readable endpoint means no check; let Informant run and report the problem itself
        if (endpoints.Count == 0)
        {
            _backoff.Reset(key);
            return true;
        }

        foreach (string endpoint in endpoints)
        {
            if (await _healthChecker.IsReachableAsync(endpoint, stoppingToken))
            {
                _backoff.Reset(key);
                return true;
            }
        }

        TimeSpan delay = _backoff.NextDelay(key);
        Log.Warning("All model endpoints for {Job} are unreachable ({Endpoints}); re-queueing in {Delay} (attempt {Attempt})", request.Job.Name, string.Join(", ", endpoints), delay,
            _backoff.AttemptCount(key));
        ScheduleRetry(request.Job, delay, stoppingToken);

        return false;
    }

    private void ScheduleRetry(ReviewJobConfig job, TimeSpan delay, CancellationToken stoppingToken)
    {
        // Non-blocking re-enqueue: the dispatcher stays free to service other repositories during one endpoint's outage,
        // and queue coalescing means a burst of retries for the same repository collapses to one
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
                _queue.Enqueue(job, "endpoint back-off retry");
            }
            catch (OperationCanceledException)
            {
                // shutting down; the in-memory queue is discarded by design
            }
        }, CancellationToken.None);
    }

    private void RecordHistory(ReviewRequest request, DateTimeOffset startedUtc, ReviewRunOutcome? outcome, string? abortReason = null)
    {
        string result = outcome is null ? "aborted" : outcome.TimedOut ? "timed-out" : outcome.Succeeded ? "completed" : "failed";

        _history.Append(new HistoryEntry
        {
            Job = request.Job.Name,
            Trigger = request.Reason,
            StartedUtc = startedUtc.ToString("O"),
            DurationSeconds = Math.Round((outcome?.Duration ?? DateTimeOffset.UtcNow - startedUtc).TotalSeconds, 1),
            ExitCode = outcome?.ExitCode,
            TimedOut = outcome?.TimedOut ?? false,
            Outcome = result,
            ReportPath = outcome?.ReportPath,
            MaxSeverity = RunHistoryStore.TryReadMaxSeverity(outcome?.ReportPath) ?? (abortReason is null ? null : "unknown")
        });
    }

    private static void LogOutcome(ReviewRequest request, ReviewRunOutcome outcome)
    {
        if (outcome.TimedOut)
        {
            Log.Error("Review of {Job} TIMED OUT after {Duration:hh\\:mm\\:ss}; the child process tree was killed", request.Job.Name, outcome.Duration);
        }
        else if (outcome.Error is not null)
        {
            Log.Error("Review of {Job} FAILED to launch after {Duration:hh\\:mm\\:ss}: {Error}", request.Job.Name, outcome.Duration, outcome.Error);
        }
        else if (outcome.Succeeded)
        {
            Log.Information("Review of {Job} completed: exit 0, duration {Duration:hh\\:mm\\:ss}, report {Report}", request.Job.Name, outcome.Duration, outcome.ReportPath ?? "(not discovered)");
        }
        else
        {
            Log.Error("Review of {Job} FAILED: exit {ExitCode}, duration {Duration:hh\\:mm\\:ss}, report {Report}", request.Job.Name, outcome.ExitCode, outcome.Duration, outcome.ReportPath ?? "(not discovered)");
        }
    }
}
