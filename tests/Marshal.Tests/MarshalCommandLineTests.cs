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
