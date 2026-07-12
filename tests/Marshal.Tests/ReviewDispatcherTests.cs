namespace Marshal.Tests;

/// <summary>Stub runner: records concurrency and run counts without launching anything</summary>
internal sealed class StubRunner : IInformantRunner
{
    private int _concurrent;
    private int _maxConcurrent;
    private int _totalRuns;

    /// <summary>How long each stubbed run takes</summary>
    public TimeSpan RunDuration { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Highest number of simultaneous runs observed</summary>
    public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

    /// <summary>Total completed runs</summary>
    public int TotalRuns => Volatile.Read(ref _totalRuns);

    /// <inheritdoc />
    public async Task<ReviewRunOutcome> RunAsync(ReviewJobConfig job, CancellationToken cancellationToken)
    {
        int current = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _maxConcurrent, current);

        await Task.Delay(RunDuration, cancellationToken);

        Interlocked.Decrement(ref _concurrent);
        Interlocked.Increment(ref _totalRuns);
        return new ReviewRunOutcome(0, RunDuration, false, null, null);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}

/// <summary>Stub checker: the first <see cref="MissesBeforeReachable"/> checks report unreachable, then reachable</summary>
internal sealed class StubHealthChecker : IEndpointHealthChecker
{
    private int _calls;

    /// <summary>How many initial checks report unreachable before the endpoint comes back</summary>
    public int MissesBeforeReachable { get; init; }

    /// <summary>Total checks made</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls);
        return Task.FromResult(call > MissesBeforeReachable);
    }
}

public sealed class ReviewDispatcherTests
{
    [Fact]
    public async Task ExecutesStrictlySeriallyAndDrainsTheQueue()
    {
        var queue = new ReviewQueue();
        var runner = new StubRunner();
        ReviewDispatcher dispatcher = CreateDispatcher(queue, runner, new StubHealthChecker());

        for (int index = 0; index < 5; index++)
        {
            queue.Enqueue(new ReviewJobConfig { Name = $"job{index}", InformantConfigPath = $@"C:\jobs\repo{index}\informant.json" }, "test");
        }

        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.TotalRuns == 5, TimeSpan.FromSeconds(10));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(5, runner.TotalRuns);
        Assert.Equal(1, runner.MaxConcurrent);
        Assert.Equal(0, queue.WaitingCount);
    }

    [Fact]
    public async Task TriggersDuringARunProduceExactlyOneRerun()
    {
        var queue = new ReviewQueue();
        var runner = new StubRunner { RunDuration = TimeSpan.FromMilliseconds(250) };
        ReviewDispatcher dispatcher = CreateDispatcher(queue, runner, new StubHealthChecker());
        var job = new ReviewJobConfig { Name = "hot", InformantConfigPath = @"C:\jobs\hot\informant.json" };

        queue.Enqueue(job, "initial");
        await dispatcher.StartAsync(CancellationToken.None);

        // Let the first run begin, then burst three triggers while it is still running
        await WaitUntilAsync(() => runner.MaxConcurrent >= 1, TimeSpan.FromSeconds(5));
        queue.Enqueue(job, "burst 1");
        queue.Enqueue(job, "burst 2");
        queue.Enqueue(job, "burst 3");

        await WaitUntilAsync(() => runner.TotalRuns == 2, TimeSpan.FromSeconds(10));
        await Task.Delay(400);
        await dispatcher.StopAsync(CancellationToken.None);

        // The burst collapsed into a single rerun: two runs total, never three
        Assert.Equal(2, runner.TotalRuns);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task CompletedRunsAreRecordedInHistory()
    {
        using var directory = new TempDirectory();
        string historyPath = Path.Combine(directory.Path, "history.jsonl");
        var history = new RunHistoryStore(historyPath);

        var queue = new ReviewQueue();
        var runner = new StubRunner();
        var dispatcher = new ReviewDispatcher(queue, runner, new StubHealthChecker(), new BackoffTracker(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15)), history);

        queue.Enqueue(new ReviewJobConfig { Name = "recorded", InformantConfigPath = @"C:\jobs\recorded\informant.json" }, "test-trigger");
        await dispatcher.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => runner.TotalRuns == 1, TimeSpan.FromSeconds(10));
        await dispatcher.StopAsync(CancellationToken.None);

        IReadOnlyList<HistoryEntry> recent = history.ReadRecent(10);
        HistoryEntry entry = Assert.Single(recent);
        Assert.Equal("recorded", entry.Job);
        Assert.Equal("test-trigger", entry.Trigger);
        Assert.Equal("completed", entry.Outcome);
        Assert.Equal(0, entry.ExitCode);
    }

    [Fact]
    public async Task UnreachableEndpointDefersTheRunThenRunsWhenItRecovers()
    {
        using var directory = new TempDirectory();
        string configPath = Path.Combine(directory.Path, "informant.json");
        File.WriteAllText(configPath, """{ "modelEndpoint": "http://localhost:9/v1" }""");

        var queue = new ReviewQueue();
        var runner = new StubRunner();
        var checker = new StubHealthChecker { MissesBeforeReachable = 1 };
        ReviewDispatcher dispatcher = CreateDispatcher(queue, runner, checker, new BackoffTracker(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(200)));

        queue.Enqueue(new ReviewJobConfig { Name = "flaky", InformantConfigPath = configPath }, "initial");
        await dispatcher.StartAsync(CancellationToken.None);

        // First check misses and defers the run; the scheduled retry re-enqueues, the second check hits, the run happens
        await WaitUntilAsync(() => runner.TotalRuns == 1, TimeSpan.FromSeconds(10));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Equal(1, runner.TotalRuns);
        Assert.True(checker.Calls >= 2, $"expected at least two health checks, saw {checker.Calls}");
    }

    private static ReviewDispatcher CreateDispatcher(ReviewQueue queue, IInformantRunner runner, IEndpointHealthChecker checker, BackoffTracker? backoff = null) =>
        new(queue, runner, checker, backoff ?? new BackoffTracker(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15)), new RunHistoryStore(Path.Combine(Path.GetTempPath(), "marshal-history-" + Guid.NewGuid().ToString("N") + ".jsonl")));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached in time");
            }

            await Task.Delay(10);
        }
    }
}
