namespace BugSwatter.Common;

/// <summary>Stable machine-wide process coordination names and exit codes</summary>
public static class BugSwatterProcessCoordination
{
    /// <summary>Machine-wide lock held for the lifetime of Marshal run</summary>
    public const string MarshalInstanceLockName = "BugSwatter.Marshal.Instance";

    /// <summary>Machine-wide lock held while Informant performs review or model verification work</summary>
    public const string InformantReviewLockName = "BugSwatter.Informant.Review";

    /// <summary>Process exit code used when a machine-wide BugSwatter operation is already active</summary>
    public const int AlreadyRunningExitCode = 75;
}

/// <summary>Creation-based machine-wide lock that remains active while its named mutex handle is open</summary>
public sealed class MachineWideProcessLock : IDisposable
{
    private Mutex? _mutex;

    private MachineWideProcessLock(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>Attempts to create a lock visible across operating-system users and login sessions</summary>
    /// <returns>The acquired lock, or null when another process already holds the same name</returns>
    public static MachineWideProcessLock? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains((char)92, StringComparison.Ordinal))
        {
            throw new ArgumentException("Machine-wide lock names cannot contain a backslash.", nameof(name));
        }

        var options = new NamedWaitHandleOptions { CurrentSessionOnly = false, CurrentUserOnly = false };
        var mutex = new Mutex(false, name, options, out bool createdNew);
        if (createdNew)
        {
            return new MachineWideProcessLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _mutex, null)?.Dispose();
}
