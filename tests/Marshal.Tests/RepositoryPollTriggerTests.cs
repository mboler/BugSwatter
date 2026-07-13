using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TestGit = BugSwatter.TestSupport.TestGit;
using TestRepository = BugSwatter.TestSupport.TestRepository;

namespace Marshal.Tests;

public sealed class RepositoryPollTriggerTests : IDisposable
{
    private const string RepositoryUrl = "https://example.test/owner/repository.git";
    private const string Branch = "main";
    private const string BaselineSha = "1111111111111111111111111111111111111111";
    private const string RemoteSha = "2222222222222222222222222222222222222222";

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task MissingBaselineEnqueuesInitialReview()
    {
        ReviewJobConfig job = CreateJob();
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(RemoteSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await trigger.PollOnceAsync(job, CancellationToken.None);

        ReviewRequest request = Assert.Single(queue.SnapshotWaiting());
        Assert.Contains("no completed baseline", request.Reason);
        Assert.Equal(1, reader.CallCount);
    }

    [Fact]
    public async Task MatchingCompletedBaselineDoesNotEnqueue()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(BaselineSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(0, queue.WaitingCount);
        Assert.Equal(1, reader.CallCount);
    }

    [Fact]
    public async Task ChangedRemoteTipEnqueuesReview()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        RepositoryPollTrigger trigger = CreateTrigger(queue, new FakeTipReader(RemoteSha), job);

        await trigger.PollOnceAsync(job, CancellationToken.None);

        ReviewRequest request = Assert.Single(queue.SnapshotWaiting());
        Assert.Contains(RemoteSha[..12], request.Reason);
        Assert.Contains(BaselineSha[..12], request.Reason);
    }

    [Fact]
    public async Task CompletedReviewBaselinePreventsAnotherPollReview()
    {
        ReviewJobConfig job = CreateJob();
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(RemoteSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);
        await trigger.PollOnceAsync(job, CancellationToken.None);
        await queue.TakeNextAsync(CancellationToken.None);

        RepositoryPollTarget target = JobConfigReader.TryReadRepositoryPollTarget(job.InformantConfigPath)!;
        WriteBaseline(target.StateFilePath, RemoteSha);
        queue.CompleteRunning();
        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(0, queue.WaitingCount);
        Assert.Equal(2, reader.CallCount);
    }

    [Fact]
    public async Task FailedReviewWithoutAdvancedBaselineRetriesOnNextPoll()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(RemoteSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);
        await trigger.PollOnceAsync(job, CancellationToken.None);
        await queue.TakeNextAsync(CancellationToken.None);

        queue.CompleteRunning();
        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(1, queue.WaitingCount);
        Assert.Equal(2, reader.CallCount);
    }

    [Fact]
    public async Task AlreadyQueuedJobSkipsRemoteCallAndDoesNotDuplicate()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        queue.Enqueue(job, "existing trigger");
        var reader = new FakeTipReader(RemoteSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(1, queue.WaitingCount);
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public async Task RunningJobSkipsRemoteCallAndDoesNotFlagRerun()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        queue.Enqueue(job, "existing trigger");
        await queue.TakeNextAsync(CancellationToken.None);
        var reader = new FakeTipReader(RemoteSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(0, reader.CallCount);
        Assert.False(queue.CompleteRunning());
    }

    [Fact]
    public async Task GitFailureDoesNotEnqueueAndNextPollCanRecover()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(new IOException("network unavailable"));
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await trigger.PollOnceAsync(job, CancellationToken.None);
        Assert.Equal(0, queue.WaitingCount);

        reader.Result = RemoteSha;
        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(1, queue.WaitingCount);
        Assert.Equal(2, reader.CallCount);
    }

    [Fact]
    public async Task CorruptBaselineDoesNotCallGitOrEnqueue()
    {
        ReviewJobConfig job = CreateJob();
        RepositoryPollTarget target = JobConfigReader.TryReadRepositoryPollTarget(job.InformantConfigPath)!;
        File.WriteAllText(target.StateFilePath, "not-json");
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(RemoteSha);
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await trigger.PollOnceAsync(job, CancellationToken.None);

        Assert.Equal(0, queue.WaitingCount);
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public async Task FakeClockFiresStartupAndFiveMinuteOccurrencesOnly()
    {
        ReviewJobConfig job = CreateJob(BaselineSha, "0 */5 * * * *");
        var queue = new ReviewQueue();
        var reader = new FakeTipReader(BaselineSha);
        var clock = new TrackingFakeTimeProvider(new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero));
        var config = new MarshalConfig { Jobs = [job] };
        var trigger = new RepositoryPollTrigger(queue, config, reader, clock);

        await trigger.StartAsync(CancellationToken.None);
        await WaitForAsync(() => reader.CallCount == 1);
        await WaitForAsync(() => clock.TimerCount == 1);

        clock.Advance(TimeSpan.FromMinutes(4));
        await Task.Yield();
        Assert.Equal(1, reader.CallCount);

        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => reader.CallCount == 2);
        await WaitForAsync(() => clock.TimerCount == 2);

        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => reader.CallCount == 3);

        await trigger.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationFromTipReaderPropagates()
    {
        ReviewJobConfig job = CreateJob(BaselineSha);
        var queue = new ReviewQueue();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var reader = new CancellingTipReader();
        RepositoryPollTrigger trigger = CreateTrigger(queue, reader, job);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trigger.PollOnceAsync(job, source.Token));
    }

    [Fact]
    public async Task GitReaderReturnsTipFromBareRemote()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        string expectedSha = await repository.CommitFileAsync("source.txt", "content", "initial");
        var target = new RepositoryPollTarget(repository.RemotePath, "main", TestGit.ExecutablePath, Path.Combine(repository.Root, "state.json"));
        var reader = new GitRepositoryTipReader();

        string actualSha = await reader.ReadTipAsync(target, CancellationToken.None);

        Assert.Equal(expectedSha, actualSha);
    }

    [Fact]
    public async Task GitReaderRejectsMissingBranch()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("source.txt", "content", "initial");
        var target = new RepositoryPollTarget(repository.RemotePath, "missing", TestGit.ExecutablePath, Path.Combine(repository.Root, "state.json"));
        var reader = new GitRepositoryTipReader();

        await Assert.ThrowsAsync<BugSwatter.Git.GitOperationException>(() => reader.ReadTipAsync(target, CancellationToken.None));
    }

    private ReviewJobConfig CreateJob(string? baselineSha = null, string schedule = RepositoryPollSettings.DefaultSchedule)
    {
        string configPath = Path.Combine(_directory.Path, $"informant-{Guid.NewGuid():N}.json");
        string statePath = Path.Combine(_directory.Path, $"state-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            repositoryUrl = RepositoryUrl,
            branch = Branch,
            gitExecutablePath = "git",
            stateFilePath = statePath
        }));

        if (baselineSha is not null)
        {
            WriteBaseline(statePath, baselineSha);
        }

        return new ReviewJobConfig { Name = "repository", InformantConfigPath = configPath, Poll = new RepositoryPollSettings { Schedule = schedule } };
    }

    private static RepositoryPollTrigger CreateTrigger(ReviewQueue queue, IRepositoryTipReader reader, ReviewJobConfig job) =>
        new(queue, new MarshalConfig { Jobs = [job] }, reader, TimeProvider.System);

    private static void WriteBaseline(string statePath, string baselineSha)
    {
        File.WriteAllText(statePath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [$"{RepositoryUrl}|{Branch}"] = new { sha = baselineSha, updatedUtc = DateTimeOffset.UtcNow }
        }));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeTipReader : IRepositoryTipReader
    {
        private int _callCount;

        public FakeTipReader(object result)
        {
            Result = result;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public object Result { get; set; }

        public Task<string> ReadTipAsync(RepositoryPollTarget target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Result is Exception exception ? Task.FromException<string>(exception) : Task.FromResult((string)Result);
        }
    }

    private sealed class TrackingFakeTimeProvider : FakeTimeProvider
    {
        private int _timerCount;

        public TrackingFakeTimeProvider(DateTimeOffset startDateTime) : base(startDateTime)
        {
        }

        public int TimerCount => Volatile.Read(ref _timerCount);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timerCount);
            return timer;
        }
    }

    private sealed class CancellingTipReader : IRepositoryTipReader
    {
        public Task<string> ReadTipAsync(RepositoryPollTarget target, CancellationToken cancellationToken) => Task.FromCanceled<string>(cancellationToken);
    }
}
