namespace Marshal.Tests;

public sealed class ChangeDebouncerTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Fact]
    public void DoesNotFireWithoutAnyChange()
    {
        var debouncer = new ChangeDebouncer();
        Assert.False(debouncer.ShouldFire(DateTime.UtcNow, Window));
    }

    [Fact]
    public void BurstOfChangesCoalescesIntoOneFiringAfterQuiet()
    {
        var debouncer = new ChangeDebouncer();
        DateTime start = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        // A checkout touching many files over 30 seconds
        for (int second = 0; second < 30; second++)
        {
            debouncer.OnChange(start.AddSeconds(second));
        }

        // Last change was at start+29s; the window has elapsed exactly at start+29s+Window ("at least" semantics).
        // The window is measured from the LAST change: a first-change-based debouncer would already fire at start+Window,
        // which is before the second probe below, so that probe would fail on such a bug
        Assert.False(debouncer.ShouldFire(start.AddSeconds(31), Window));
        Assert.False(debouncer.ShouldFire(start.AddSeconds(29).Add(Window).AddSeconds(-1), Window));
        Assert.True(debouncer.ShouldFire(start.AddSeconds(29).Add(Window), Window));

        // The fire above consumed the settle. ShouldFire is a fire-once latch, not a time predicate: the poll loop calls it
        // every couple of seconds, and without the latch a settled burst would enqueue a review on every poll forever.
        // Only a new OnChange re-arms it, so every later poll without one must stay quiet
        Assert.False(debouncer.ShouldFire(start.AddSeconds(31).Add(Window), Window));
        Assert.False(debouncer.ShouldFire(start.AddMinutes(40), Window));
        Assert.False(debouncer.ShouldFire(start.AddDays(1), Window));
    }

    [Fact]
    public void NewChangeAfterFiringArmsItAgain()
    {
        var debouncer = new ChangeDebouncer();
        DateTime start = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

        debouncer.OnChange(start);
        Assert.True(debouncer.ShouldFire(start.Add(Window), Window));

        debouncer.OnChange(start.Add(Window).AddMinutes(1));
        Assert.False(debouncer.ShouldFire(start.Add(Window).AddMinutes(2), Window));
        Assert.True(debouncer.ShouldFire(start.Add(Window).AddMinutes(1).Add(Window), Window));
    }
}
