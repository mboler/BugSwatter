namespace Marshal.Tests;

public sealed class ScheduleAndInstallerTests
{
    [Fact]
    public void NextOccurrenceIsTodayWhenTimeIsStillAhead()
    {
        var now = new DateTime(2026, 7, 10, 8, 0, 0);
        Assert.Equal(new DateTime(2026, 7, 10, 15, 30, 0), ScheduleTrigger.NextOccurrence(now, new TimeOnly(15, 30)));
    }

    [Fact]
    public void NextOccurrenceIsTomorrowWhenTimeHasPassed()
    {
        var now = new DateTime(2026, 7, 10, 16, 0, 0);
        Assert.Equal(new DateTime(2026, 7, 11, 15, 30, 0), ScheduleTrigger.NextOccurrence(now, new TimeOnly(15, 30)));
    }

    [Fact]
    public void NextOccurrenceAtTheExactMomentRollsToTomorrow()
    {
        var now = new DateTime(2026, 7, 10, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 7, 11, 15, 30, 0), ScheduleTrigger.NextOccurrence(now, new TimeOnly(15, 30)));
    }

    [Fact]
    public void WindowsBinPathQuotesExecutableAndConfig()
    {
        string binPath = ServiceInstaller.BuildWindowsBinPath(@"C:\deploy\Marshal.exe", @"C:\deploy\marshal.json");
        Assert.Equal("\"C:\\deploy\\Marshal.exe\" run --config \"C:\\deploy\\marshal.json\"", binPath);
    }

    [Fact]
    public void WindowsServiceMetadataUsesBugSwatterNameAndReviewDescription()
    {
        Assert.Equal("BugSwatter Marshal Service", ServiceInstaller.WindowsServiceDisplayName);
        Assert.Equal("Watches repositories and dispatches Informant code reviews", ServiceInstaller.WindowsServiceDescription);
    }

    [Fact]
    public void SystemdUnitCarriesExecStartRestartAndInstallTarget()
    {
        string unit = ServiceInstaller.BuildSystemdUnit("/opt/marshal/Marshal", "/opt/marshal/marshal.json");
        Assert.Contains("ExecStart=\"/opt/marshal/Marshal\" run --config \"/opt/marshal/marshal.json\"", unit);
        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains("WantedBy=multi-user.target", unit);
        Assert.Contains("Type=notify", unit);
        Assert.DoesNotContain("User=", unit);
    }

    [Fact]
    public void SystemdUnitQuotesPathsAndCarriesCustomUser()
    {
        string unit = ServiceInstaller.BuildSystemdUnit("/opt/Bug Swatter/Marshal", "/etc/Bug Swatter/marshal.json", "bugswatter");

        Assert.Contains("User=\"bugswatter\"", unit);
        Assert.Contains("ExecStart=\"/opt/Bug Swatter/Marshal\" run --config \"/etc/Bug Swatter/marshal.json\"", unit);
    }

    [Fact]
    public void SystemdUnitRejectsLineBreakInjection()
    {
        Assert.Throws<MarshalFatalException>(() => ServiceInstaller.BuildSystemdUnit("/opt/marshal/Marshal", "/opt/marshal/marshal.json", "user\nRestart=no"));
    }
}
