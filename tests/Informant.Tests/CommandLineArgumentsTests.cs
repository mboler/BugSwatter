namespace Informant.Tests;

public sealed class CommandLineArgumentsTests
{
    [Fact]
    public void NoArgumentsDefaultsToRunWithoutConfigPath()
    {
        var arguments = CommandLineArguments.Parse([]);
        Assert.Equal("run", arguments.Command);
        Assert.Null(arguments.ConfigPath);
        Assert.Equal(ProgressOutput.None, arguments.ProgressOutput);
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
    public void JsonProgressOutputParsesWithoutChangingTheCommand()
    {
        var arguments = CommandLineArguments.Parse(["--progress", "json", "--config", "cfg.json"]);

        Assert.Equal("run", arguments.Command);
        Assert.Equal("cfg.json", arguments.ConfigPath);
        Assert.Equal(ProgressOutput.Json, arguments.ProgressOutput);
    }

    [Theory]
    [InlineData("--progress requires")]
    [InlineData("Unsupported --progress")]
    public void InvalidProgressOutputIsFatal(string expectedMessage)
    {
        string[] arguments = expectedMessage.StartsWith("Unsupported", StringComparison.Ordinal) ? ["--progress", "xml"] : ["--progress"];

        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => CommandLineArguments.Parse(arguments));

        Assert.Contains(expectedMessage, ex.Message);
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
