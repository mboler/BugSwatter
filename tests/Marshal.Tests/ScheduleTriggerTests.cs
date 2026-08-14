using Microsoft.Extensions.Time.Testing;

namespace Marshal.Tests;

/// <summary>Daily schedule behavior under normal and adjusted wall clocks</summary>
public sealed class ScheduleTriggerTests
{
    /// <summary>Verifies that a backward clock adjustment cannot fire an occurrence early or twice</summary>
    [Fact]
    public async Task BackwardClockAdjustmentCannotFireAnOccurrenceEarlyOrTwice()
    {
        var queue = new ReviewQueue();
        var clock = CreateClock(new DateTimeOffset(2026, 8, 14, 1, 59, 50, TimeSpan.Zero));
        ReviewJobConfig job = CreateJob("daily", "02:00");
        var trigger = new ScheduleTrigger(queue, new MarshalConfig { Jobs = [job] }, clock);

        await trigger.StartAsync(CancellationToken.None);
        await WaitForAsync(() => clock.TimerCount == 1);

        clock.AdjustTime(new DateTimeOffset(2026, 8, 14, 1, 59, 45, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(10));
        await WaitForAsync(() => clock.TimerCount == 2);
        Assert.Equal(0, queue.WaitingCount);

        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => queue.WaitingCount == 1);
        await queue.TakeNextAsync(CancellationToken.None);
        await WaitForAsync(() => clock.TimerCount >= 3);

        int timerCountBeforeRollback = clock.TimerCount;
        clock.AdjustTime(new DateTimeOffset(2026, 8, 14, 1, 59, 0, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() => clock.TimerCount > timerCountBeforeRollback);

        Assert.False(queue.CompleteRunning());
        await trigger.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies that distinct jobs sharing a schedule time are each queued</summary>
    [Fact]
    public async Task JobsSharingTheSameTimeEachEnqueueOnce()
    {
        var queue = new ReviewQueue();
        var clock = CreateClock(new DateTimeOffset(2026, 8, 14, 1, 59, 59, TimeSpan.Zero));
        ReviewJobConfig first = CreateJob("first", "02:00");
        ReviewJobConfig second = CreateJob("second", "02:00");
        var trigger = new ScheduleTrigger(queue, new MarshalConfig { Jobs = [first, second] }, clock);

        await trigger.StartAsync(CancellationToken.None);
        await WaitForAsync(() => clock.TimerCount == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => queue.WaitingCount == 2);

        Assert.Equal(["first", "second"], queue.SnapshotWaiting().Select(request => request.Job.Name).Order().ToArray());
        await trigger.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies that a forward clock adjustment fires one skipped occurrence</summary>
    [Fact]
    public async Task ForwardClockAdjustmentFiresTheMissedOccurrenceOnce()
    {
        var queue = new ReviewQueue();
        var clock = CreateClock(new DateTimeOffset(2026, 8, 14, 1, 0, 0, TimeSpan.Zero));
        ReviewJobConfig job = CreateJob("daily", "02:00");
        var trigger = new ScheduleTrigger(queue, new MarshalConfig { Jobs = [job] }, clock);

        await trigger.StartAsync(CancellationToken.None);
        await WaitForAsync(() => clock.TimerCount == 1);
        clock.AdjustTime(new DateTimeOffset(2026, 8, 14, 3, 0, 0, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitForAsync(() => queue.WaitingCount == 1);
        await queue.TakeNextAsync(CancellationToken.None);

        Assert.False(queue.CompleteRunning());
        await trigger.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies that a late wake fires once instead of replaying every missed day</summary>
    [Fact]
    public async Task LateWakeFiresOnceAndSkipsMissedDailyBacklog()
    {
        var queue = new ReviewQueue();
        var clock = CreateClock(new DateTimeOffset(2026, 8, 14, 1, 59, 59, TimeSpan.Zero));
        ReviewJobConfig job = CreateJob("daily", "02:00");
        var trigger = new ScheduleTrigger(queue, new MarshalConfig { Jobs = [job] }, clock);

        await trigger.StartAsync(CancellationToken.None);
        await WaitForAsync(() => clock.TimerCount == 1);
        clock.Advance(TimeSpan.FromDays(3));
        await WaitForAsync(() => queue.WaitingCount == 1);
        await queue.TakeNextAsync(CancellationToken.None);

        Assert.False(queue.CompleteRunning());
        await trigger.StopAsync(CancellationToken.None);
    }

    private static TrackingFakeTimeProvider CreateClock(DateTimeOffset start)
    {
        var clock = new TrackingFakeTimeProvider(start);
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);
        return clock;
    }

    private static ReviewJobConfig CreateJob(string name, params string[] schedule) =>
        new() { Name = name, InformantConfigPath = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}", "informant.json"), Schedule = schedule };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
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
}
