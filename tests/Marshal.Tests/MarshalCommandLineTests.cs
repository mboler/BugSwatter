namespace Marshal.Tests;

public sealed class MarshalCommandLineTests
{
    [Fact]
    public void DefaultsToRunWithoutFlags()
    {
        var line = MarshalCommandLine.Parse([]);
        Assert.Equal("run", line.Command);
        Assert.Null(line.ConfigPath);
        Assert.False(line.ReviewAll);
        Assert.Null(line.ServiceUser);
        Assert.Null(line.ServicePasswordReference);
    }

    [Fact]
    public void ParsesRunWithConfigAndReviewAll()
    {
        var line = MarshalCommandLine.Parse(["run", "--config", @"C:\marshal\marshal.json", "--review-all"]);
        Assert.Equal("run", line.Command);
        Assert.Equal(@"C:\marshal\marshal.json", line.ConfigPath);
        Assert.True(line.ReviewAll);
    }

    [Fact]
    public void ParsesInstallWithConfig()
    {
        var line = MarshalCommandLine.Parse(["install", "--config", "m.json"]);
        Assert.Equal("install", line.Command);
        Assert.Equal("m.json", line.RequireConfigPath());
        Assert.False(line.UseScExe);
    }

    [Fact]
    public void ParsesUseScFlagForInstallAndRemove()
    {
        Assert.True(MarshalCommandLine.Parse(["install", "--config", "m.json", "--use-sc"]).UseScExe);
        Assert.True(MarshalCommandLine.Parse(["remove", "--use-sc"]).UseScExe);
    }

    [Fact]
    public void ParsesCustomServiceAccountWithReferencedPassword()
    {
        var line = MarshalCommandLine.Parse(["install", "--config", "m.json", "--service-user", @".\BugSwatter", "--service-password", "file:service-password.txt"]);

        Assert.Equal(@".\BugSwatter", line.ServiceUser);
        Assert.Equal("file:service-password.txt", line.ServicePasswordReference);
        Assert.False(line.UseScExe);
    }

    [Fact]
    public void CustomServiceAccountRejectsScExePath()
    {
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalCommandLine.Parse(["install", "--config", "m.json", "--service-user", @".\BugSwatter", "--use-sc"]));
        Assert.Contains("Service Control Manager API", ex.Message);
    }

    [Fact]
    public void ServicePasswordRequiresUserAndSecretReference()
    {
        Assert.Throws<MarshalFatalException>(() => MarshalCommandLine.Parse(["install", "--config", "m.json", "--service-password", "env:MARSHAL_SERVICE_PASSWORD"]));
        Assert.Throws<MarshalFatalException>(() => MarshalCommandLine.Parse(["install", "--config", "m.json", "--service-user", @".\BugSwatter", "--service-password", "literal-password"]));
    }

    [Fact]
    public void ServiceAccountOptionsAreInstallOnly()
    {
        Assert.Throws<MarshalFatalException>(() => MarshalCommandLine.Parse(["run", "--config", "m.json", "--service-user", @".\BugSwatter"]));
    }

    [Fact]
    public void RequireConfigPathFailsWhenAbsent()
    {
        var line = MarshalCommandLine.Parse(["run"]);
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => line.RequireConfigPath());
        Assert.Contains("--config", ex.Message);
    }

    [Fact]
    public void ConfigWithoutValueIsFatal()
    {
        Assert.Throws<MarshalFatalException>(() => MarshalCommandLine.Parse(["run", "--config"]));
    }

    [Fact]
    public void UnexpectedSecondCommandIsFatal()
    {
        Assert.Throws<MarshalFatalException>(() => MarshalCommandLine.Parse(["run", "extra"]));
    }
}
