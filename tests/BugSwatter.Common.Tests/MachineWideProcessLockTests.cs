namespace BugSwatter.Common.Tests;

/// <summary>Machine-wide process lock behavior</summary>
public sealed class MachineWideProcessLockTests
{
    /// <summary>Verifies that a lock can be reacquired only after its current owner releases it</summary>
    [Fact]
    public void SecondAcquisitionFailsUntilFirstLockIsDisposed()
    {
        string name = UniqueName();
        MachineWideProcessLock first = Assert.IsType<MachineWideProcessLock>(MachineWideProcessLock.TryAcquire(name));

        Assert.Null(MachineWideProcessLock.TryAcquire(name));

        first.Dispose();
        using MachineWideProcessLock reacquired = Assert.IsType<MachineWideProcessLock>(MachineWideProcessLock.TryAcquire(name));
    }

    /// <summary>Verifies that concurrent acquisition attempts produce exactly one owner</summary>
    [Fact]
    public async Task ConcurrentAcquisitionsProduceOneOwner()
    {
        string name = UniqueName();
        Task<MachineWideProcessLock?>[] attempts = [.. Enumerable.Range(0, 16).Select(_ => Task.Run(() => MachineWideProcessLock.TryAcquire(name)))];

        MachineWideProcessLock?[] results = await Task.WhenAll(attempts);

        MachineWideProcessLock owner = Assert.Single(results.OfType<MachineWideProcessLock>());
        owner.Dispose();
    }

    /// <summary>Verifies that nonportable lock names are rejected</summary>
    [Fact]
    public void BackslashIsRejectedBecauseItIsNotPortable()
    {
        string name = string.Concat("BugSwatter", (char)92, "Invalid");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => MachineWideProcessLock.TryAcquire(name));

        Assert.Equal("name", exception.ParamName);
    }

    private static string UniqueName() => $"BugSwatter.Tests.{Guid.NewGuid():N}";
}
