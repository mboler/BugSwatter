using System.Collections.Concurrent;
using BugSwatter.Common;
using BugSwatter.Git;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Marshal;

/// <summary>Reads the current remote branch tip for a configured repository</summary>
public interface IRepositoryTipReader
{
    /// <summary>Returns the remote branch tip SHA, or throws when Git cannot read an unambiguous tip</summary>
    Task<string> ReadTipAsync(RepositoryPollTarget target, CancellationToken cancellationToken);
}

/// <summary>Git-backed remote-tip reader using ls-remote without changing or fetching a working tree</summary>
public sealed class GitRepositoryTipReader : IRepositoryTipReader
{
    /// <inheritdoc />
    public async Task<string> ReadTipAsync(RepositoryPollTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        string expectedReference = $"refs/heads/{target.Branch}";
        var git = new GitRunner(target.GitExecutablePath);
        GitResult result = await git.RunAsync(cancellationToken, "ls-remote", "--exit-code", "--heads", target.RepositoryUrl, expectedReference);
        if (result.ExitCode != 0)
        {
            throw new GitOperationException($"git ls-remote failed with exit code {result.ExitCode}: {TextSummary.Create(result.StandardError, 1000)}");
        }

        string[] lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1)
        {
            throw new GitOperationException($"git ls-remote returned {lines.Length} branch-tip records; expected exactly one");
        }

        string[] fields = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || !string.Equals(fields[1], expectedReference, StringComparison.Ordinal) || !IsObjectId(fields[0]))
        {
            throw new GitOperationException("git ls-remote returned an invalid branch-tip record");
        }

        return fields[0];
    }

    private static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}

/// <summary>Outbound scheduler that compares remote branch tips with Informant's last completed-review baselines and enqueues only changed, idle jobs</summary>
public sealed class RepositoryPollTrigger : BackgroundService
{
    private readonly ReviewQueue _queue;
    private readonly MarshalConfig _config;
    private readonly IRepositoryTipReader _tipReader;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, byte> _failedJobs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a polling trigger over the configured jobs and injected clock</summary>
    public RepositoryPollTrigger(ReviewQueue queue, MarshalConfig config, IRepositoryTipReader tipReader, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tipReader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _queue = queue;
        _config = config;
        _tipReader = tipReader;
        _timeProvider = timeProvider;
    }

    /// <summary>Checks one job now and enqueues it only when its remote tip differs from its last completed-review baseline</summary>
    public async Task PollOnceAsync(ReviewJobConfig job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_queue.IsQueuedOrRunning(job))
        {
            Log.Debug("Skipping repository poll for {Job}: it is already queued or running", job.Name);
            return;
        }

        string key = ReviewQueue.RepositoryKey(job);
        try
        {
            RepositoryPollTarget target = JobConfigReader.TryReadRepositoryPollTarget(job.InformantConfigPath)
                ?? throw new InvalidDataException("Informant config does not provide repositoryUrl, branch and gitExecutablePath");
            string? baselineSha = JobConfigReader.ReadBaselineSha(target);
            string remoteSha = await _tipReader.ReadTipAsync(target, cancellationToken);

            if (_failedJobs.TryRemove(key, out _))
            {
                Log.Information("Repository polling recovered for {Job}", job.Name);
            }

            if (string.Equals(remoteSha, baselineSha, StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("Repository poll for {Job}: remote tip {Tip} matches the completed-review baseline", job.Name, ShortSha(remoteSha));
                return;
            }

            if (_queue.IsQueuedOrRunning(job))
            {
                return;
            }

            string previous = baselineSha is null ? "no completed baseline" : $"baseline {ShortSha(baselineSha)}";
            _queue.Enqueue(job, $"repository poll found remote {ShortSha(remoteSha)} different from {previous}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // catch-all: one repository or credential failure must not stop polling the other configured jobs
            if (_failedJobs.TryAdd(key, 0))
            {
                Log.Warning("Repository poll failed for {Job}: {Reason}; it will retry on the next occurrence", job.Name, ex.Message);
            }
            else
            {
                Log.Debug("Repository poll still failing for {Job}: {Reason}", job.Name, ex.Message);
            }
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IEnumerable<Task> loops = _config.Jobs
            .Where(job => job.Poll is { Enabled: true })
            .Select(job => RunJobAsync(job, stoppingToken));
        return Task.WhenAll(loops);
    }

    private async Task RunJobAsync(ReviewJobConfig job, CancellationToken stoppingToken)
    {
        PollSchedule schedule = PollSchedule.Parse(job.Poll!.Schedule);
        Log.Information("Repository polling enabled for {Job} on UTC schedule {Schedule}; checking once at startup", job.Name, schedule.Expression);
        await PollOnceAsync(job, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            DateTimeOffset next = schedule.GetNextOccurrence(now);
            await Task.Delay(next - now, _timeProvider, stoppingToken);
            await PollOnceAsync(job, stoppingToken);
        }
    }

    private static string ShortSha(string sha) => sha[..Math.Min(12, sha.Length)];
}
