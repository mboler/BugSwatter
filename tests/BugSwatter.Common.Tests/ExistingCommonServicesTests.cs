using System.Text.Json;
using Serilog;

namespace BugSwatter.Common.Tests;

public sealed class ExistingCommonServicesTests : IDisposable
{
    private const string EnvironmentPrefix = "BUGSWATTER_COMMON_TEST_";

    private readonly TempDirectory _directory = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentPrefix + "Name", null);
        _directory.Dispose();
    }

    [Fact]
    public void ConfigLoaderLayersPrefixedEnvironmentOverJsonAndResolvesPaths()
    {
        string path = System.IO.Path.Combine(_directory.Path, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { name = "from-json" }));
        Environment.SetEnvironmentVariable(EnvironmentPrefix + "Name", "from-environment");

        TestConfig config = ConfigLoader.Load<TestConfig>(path, EnvironmentPrefix);

        Assert.Equal("from-environment", config.Name);
        Assert.Equal(_directory.Path, ConfigLoader.GetConfigDirectory(path));
        Assert.Equal(System.IO.Path.Combine(_directory.Path, "reports"), ConfigLoader.ResolvePath(_directory.Path, "reports"));
    }

    [Fact]
    public void ReportMarkerIsStable()
    {
        Assert.Equal("INFORMANT-REPORT:", ReportMarker.Prefix);
    }

    [Fact]
    public void LoggingSetupHonorsConsoleOverrideAndFallsBackForUnknownLevel()
    {
        string path = System.IO.Path.Combine(_directory.Path, "common.log");
        Assert.False(LoggingSetup.Initialize("not-a-level", path, consoleLogging: false));

        Log.Information("common logging test message");
        Log.CloseAndFlush();

        string log = Assert.Single(Directory.GetFiles(_directory.Path, "common*.log"));
        Assert.Contains("common logging test message", File.ReadAllText(log));
    }

    private sealed class TestConfig
    {
        public string Name { get; init; } = "";
    }
}
