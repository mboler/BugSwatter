namespace Informant.Tests;

public sealed class CommandLineArgumentsTests
{
    [Fact]
    public void NoArgumentsDefaultsToRunWithoutConfigPath()
    {
        var arguments = CommandLineArguments.Parse([]);
        Assert.Equal("run", arguments.Command);
        Assert.Null(arguments.ConfigPath);
    }

    [Fact]
    public void CommandAndConfigPathBothParse()
    {
        var arguments = CommandLineArguments.Parse(["verify", "--config", @"C:\jobs\a\informant.json"]);
        Assert.Equal("verify", arguments.Command);
        Assert.Equal(@"C:\jobs\a\informant.json", arguments.ConfigPath);
    }

    [Fact]
    public void ConfigPathWithoutCommandDefaultsToRun()
    {
        var arguments = CommandLineArguments.Parse(["--config", "cfg.json"]);
        Assert.Equal("run", arguments.Command);
        Assert.Equal("cfg.json", arguments.ConfigPath);
    }

    [Fact]
    public void ConfigOptionBeforeCommandParses()
    {
        var arguments = CommandLineArguments.Parse(["--config", "cfg.json", "verify"]);
        Assert.Equal("verify", arguments.Command);
        Assert.Equal("cfg.json", arguments.ConfigPath);
    }

    [Fact]
    public void ConfigWithoutValueIsFatal()
    {
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => CommandLineArguments.Parse(["run", "--config"]));
        Assert.Contains("--config requires", ex.Message);
    }

    [Fact]
    public void SecondPositionalArgumentIsFatal()
    {
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => CommandLineArguments.Parse(["run", "extra"]));
        Assert.Contains("Unexpected argument", ex.Message);
    }
}
