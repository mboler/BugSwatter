namespace Marshal;

/// <summary>Coalesces a burst of file-change events into a single firing once changes have settled for the quiet window. Lock-free: a git checkout touching thousands of files must not contend</summary>
public sealed class ChangeDebouncer
{
    private long _lastChangeTicks;
    private int _dirty;

    /// <summary>Records that a change happened at <paramref name="nowUtc"/></summary>
    public void OnChange(DateTime nowUtc)
    {
        Interlocked.Exchange(ref _lastChangeTicks, nowUtc.Ticks);
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Returns true exactly once after changes stop for at least <paramref name="quietWindow"/>; further changes restart the wait</summary>
    public bool ShouldFire(DateTime nowUtc, TimeSpan quietWindow)
    {
        if (Volatile.Read(ref _dirty) == 0)
        {
            return false;
        }

        long lastChange = Interlocked.Read(ref _lastChangeTicks);
        if (nowUtc.Ticks - lastChange < quietWindow.Ticks)
        {
            return false;
        }

        // Only the caller that successfully clears the flag fires; a change racing in after this point sets it again
        return Interlocked.CompareExchange(ref _dirty, 0, 1) == 1;
    }
}
