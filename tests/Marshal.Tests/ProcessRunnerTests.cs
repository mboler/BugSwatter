using System.Diagnostics;

namespace Marshal.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task StuckChildIsKilledAtTimeoutAndMarkedTimedOut()
    {
        var stopwatch = Stopwatch.StartNew();
        ProcessRunResult result = OperatingSystem.IsWindows()
            ? await SlimShadyProcessRunner.RunProcessAsync("cmd.exe", ["/c", "ping", "-n", "60", "127.0.0.1"], TimeSpan.FromSeconds(2), CancellationToken.None)
            : await SlimShadyProcessRunner.RunProcessAsync("/bin/sleep", ["60"], TimeSpan.FromSeconds(2), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"kill took {stopwatch.Elapsed}, the child was not stopped promptly");
    }

    [Fact]
    public async Task FastChildCompletesWithItsExitCode()
    {
        ProcessRunResult result = OperatingSystem.IsWindows()
            ? await SlimShadyProcessRunner.RunProcessAsync("cmd.exe", ["/c", "exit", "3"], TimeSpan.FromSeconds(30), CancellationToken.None)
            : await SlimShadyProcessRunner.RunProcessAsync("/bin/sh", ["-c", "exit 3"], TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
    }
}
