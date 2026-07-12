namespace Marshal.Tests;

public sealed class BackoffTrackerTests
{
    [Fact]
    public void DelayDoublesEachMissUpToTheCap()
    {
        var tracker = new BackoffTracker(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));

        Assert.Equal(TimeSpan.FromSeconds(10), tracker.NextDelay("repo"));
        Assert.Equal(TimeSpan.FromSeconds(20), tracker.NextDelay("repo"));
        Assert.Equal(TimeSpan.FromSeconds(40), tracker.NextDelay("repo"));
        Assert.Equal(TimeSpan.FromSeconds(60), tracker.NextDelay("repo"));
        Assert.Equal(TimeSpan.FromSeconds(60), tracker.NextDelay("repo"));
    }

    [Fact]
    public void ResetClearsTheBackoff()
    {
        var tracker = new BackoffTracker(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        tracker.NextDelay("repo");
        tracker.NextDelay("repo");
        Assert.Equal(2, tracker.AttemptCount("repo"));

        tracker.Reset("repo");

        Assert.Equal(0, tracker.AttemptCount("repo"));
        Assert.Equal(TimeSpan.FromSeconds(10), tracker.NextDelay("repo"));
    }

    [Fact]
    public void KeysBackOffIndependently()
    {
        var tracker = new BackoffTracker(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        tracker.NextDelay("a");
        tracker.NextDelay("a");

        Assert.Equal(TimeSpan.FromSeconds(10), tracker.NextDelay("b"));
        Assert.Equal(2, tracker.AttemptCount("a"));
        Assert.Equal(1, tracker.AttemptCount("b"));
    }

    [Fact]
    public void HighAttemptCountStaysAtTheCapWithoutOverflow()
    {
        var tracker = new BackoffTracker(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
        TimeSpan delay = TimeSpan.Zero;
        for (int i = 0; i < 100; i++)
        {
            delay = tracker.NextDelay("repo");
        }

        Assert.Equal(TimeSpan.FromMinutes(15), delay);
    }
}
