namespace Marshal.Tests;

public sealed class ReviewQueueTests
{
    [Fact]
    public void EnqueueAddsDistinctJobs()
    {
        var queue = new ReviewQueue();
        Assert.Equal(EnqueueResult.Enqueued, queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "test"));
        Assert.Equal(EnqueueResult.Enqueued, queue.Enqueue(Job(@"C:\jobs\b\slimshady.json"), "test"));
        Assert.Equal(2, queue.WaitingCount);
    }

    [Fact]
    public void DuplicateWaitingRepositoryCoalesces()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "first");
        Assert.Equal(EnqueueResult.CoalescedWithQueued, queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "second"));
        Assert.Equal(1, queue.WaitingCount);
    }

    [Fact]
    public void RepositoryIdentityIsCaseInsensitiveOnWindows()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\Repo\slimshady.json"), "upper");
        EnqueueResult second = queue.Enqueue(Job(@"c:\repo\SLIMSHADY.JSON"), "lower");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(EnqueueResult.CoalescedWithQueued, second);
            Assert.Equal(1, queue.WaitingCount);
        }
        else
        {
            Assert.Equal(EnqueueResult.Enqueued, second);
        }
    }

    [Fact]
    public void OverflowBeyondCapacityIsDropped()
    {
        var queue = new ReviewQueue();
        for (int index = 0; index < ReviewQueue.MaxLength; index++)
        {
            Assert.Equal(EnqueueResult.Enqueued, queue.Enqueue(Job($@"C:\jobs\repo{index}\slimshady.json"), "fill"));
        }

        Assert.Equal(EnqueueResult.DroppedQueueFull, queue.Enqueue(Job(@"C:\jobs\overflow\slimshady.json"), "overflow"));
        Assert.Equal(ReviewQueue.MaxLength, queue.WaitingCount);
    }

    [Fact]
    public async Task RunningRepositoryCollapsesTriggersIntoOneRerun()
    {
        var queue = new ReviewQueue();
        ReviewJobConfig job = Job(@"C:\jobs\a\slimshady.json");
        queue.Enqueue(job, "initial");
        ReviewRequest running = await queue.TakeNextAsync(CancellationToken.None);
        Assert.Equal("initial", running.Reason);
        Assert.Equal(0, queue.WaitingCount);

        Assert.Equal(EnqueueResult.RerunPending, queue.Enqueue(job, "while running 1"));
        Assert.Equal(EnqueueResult.RerunAlreadyPending, queue.Enqueue(job, "while running 2"));
        Assert.Equal(EnqueueResult.RerunAlreadyPending, queue.Enqueue(job, "while running 3"));
        Assert.Equal(0, queue.WaitingCount);

        Assert.True(queue.CompleteRunning());
        Assert.Equal(1, queue.WaitingCount);
    }

    [Fact]
    public async Task CompleteWithoutPendingRerunLeavesQueueAlone()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "initial");
        await queue.TakeNextAsync(CancellationToken.None);

        Assert.False(queue.CompleteRunning());
        Assert.Equal(0, queue.WaitingCount);
    }

    [Fact]
    public async Task RepositoryCanBeEnqueuedAgainAfterCompletion()
    {
        var queue = new ReviewQueue();
        ReviewJobConfig job = Job(@"C:\jobs\a\slimshady.json");
        queue.Enqueue(job, "first");
        await queue.TakeNextAsync(CancellationToken.None);
        queue.CompleteRunning();

        Assert.Equal(EnqueueResult.Enqueued, queue.Enqueue(job, "after completion"));
    }

    [Fact]
    public async Task DifferentRepositoryEnqueuesNormallyWhileAnotherRuns()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "a");
        await queue.TakeNextAsync(CancellationToken.None);

        Assert.Equal(EnqueueResult.Enqueued, queue.Enqueue(Job(@"C:\jobs\b\slimshady.json"), "b"));
        Assert.Equal(1, queue.WaitingCount);
    }

    [Fact]
    public void SnapshotListsWaitingRequestsInOrder()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "first");
        queue.Enqueue(Job(@"C:\jobs\b\slimshady.json"), "second");

        Assert.Equal([@"C:\jobs\a", @"C:\jobs\b"], queue.SnapshotWaiting().Select(request => request.Job.Name));
    }

    [Fact]
    public async Task RemoveWaitingDropsTheJobAndItsQueueSlot()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "a");
        queue.Enqueue(Job(@"C:\jobs\b\slimshady.json"), "b");

        Assert.True(queue.RemoveWaiting(@"C:\jobs\a"));
        Assert.Equal(1, queue.WaitingCount);

        // The removed job also gave up its dispatch slot, so the next request taken is b, not the removed a
        ReviewRequest next = await queue.TakeNextAsync(CancellationToken.None);
        Assert.Equal(@"C:\jobs\b", next.Job.Name);
        Assert.Equal(0, queue.WaitingCount);
    }

    [Fact]
    public void RemoveWaitingReturnsFalseWhenNotQueued()
    {
        var queue = new ReviewQueue();
        queue.Enqueue(Job(@"C:\jobs\a\slimshady.json"), "a");

        Assert.False(queue.RemoveWaiting("nonexistent"));
        Assert.Equal(1, queue.WaitingCount);
    }

    [Fact]
    public void RemovedRepositoryCanBeEnqueuedAgain()
    {
        var queue = new ReviewQueue();
        ReviewJobConfig job = Job(@"C:\jobs\a\slimshady.json");
        queue.Enqueue(job, "a");
        queue.RemoveWaiting(@"C:\jobs\a");

        Assert.Equal(EnqueueResult.Enqueued, queue.Enqueue(job, "again"));
    }

    private static ReviewJobConfig Job(string configPath) => new() { Name = System.IO.Path.GetDirectoryName(configPath) ?? configPath, SlimShadyConfigPath = configPath };
}
