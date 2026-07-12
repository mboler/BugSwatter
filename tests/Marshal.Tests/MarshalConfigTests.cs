using System.Text.Json;

namespace Marshal.Tests;

public sealed class MarshalConfigTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void LoadsValidConfigWithDefaults()
    {
        string path = WriteConfig();
        var config = MarshalConfig.Load(path);

        Assert.Equal(360, config.PerRunTimeoutMinutes);
        Assert.Equal(300, config.FileWatchDebounceSeconds);
        Assert.Equal("Information", config.LogLevel);
        ReviewJobConfig job = Assert.Single(config.Jobs);
        Assert.Equal("job-a", job.Name);
    }

    [Fact]
    public void MissingFileIsFatal()
    {
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(Path.Combine(_directory.Path, "nope.json")));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void MissingSlimShadyExecutableIsFatal()
    {
        string path = WriteConfig(mutate: values => values["slimShadyExecutable"] = Path.Combine(_directory.Path, "ghost.exe"));
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("slimShadyExecutable", ex.Message);
    }

    [Fact]
    public void InvalidScheduleTimeIsFatal()
    {
        string path = WriteConfig(schedule: ["25:99"]);
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("25:99", ex.Message);
    }

    [Fact]
    public void JobWebhookWithoutGlobalListenerIsFatal()
    {
        string path = WriteConfig(jobWebhook: true, webhookEnabled: false);
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("webhook listener is not enabled", ex.Message);
    }

    [Fact]
    public void JobWebhookWithGlobalListenerLoads()
    {
        string path = WriteConfig(jobWebhook: true, webhookEnabled: true);
        var config = MarshalConfig.Load(path);
        Assert.True(config.Webhook!.Enabled);
        Assert.True(config.WebServer!.Enabled);
        Assert.Equal(5000, config.WebServer.Port);
        Assert.Equal(WebhookProvider.GitHub, config.Jobs[0].Webhook!.Provider);
    }

    [Fact]
    public void WebhookEnabledWithoutWebServerIsFatal()
    {
        string path = WriteConfig(webhookEnabled: true, mutate: values => values.Remove("webServer"));
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("webServer block is not", ex.Message);
    }

    [Fact]
    public void OutOfRangeWebServerPortIsFatal()
    {
        string path = WriteConfig(mutate: values => values["webServer"] = new Dictionary<string, object?> { ["enabled"] = true, ["port"] = 70000 });
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("webServer.port", ex.Message);
    }

    [Fact]
    public void BareEnvironmentReferenceSecretIsFatalAtLoad()
    {
        string path = WriteConfig(jobWebhook: true, webhookEnabled: true, mutate: values => values["webhook"] = new Dictionary<string, object?> { ["enabled"] = true, ["port"] = 5100, ["gitHubSecret"] = "env:" });

        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("no variable name", ex.Message);
    }

    [Fact]
    public void SecretsResolveLiteralsAndEnvironmentReferences()
    {
        Assert.Equal("literal-value", MarshalConfig.ResolveSecret("literal-value"));
        Assert.Null(MarshalConfig.ResolveSecret(null));
        Assert.Null(MarshalConfig.ResolveSecret(""));

        Environment.SetEnvironmentVariable("MARSHAL_TEST_SECRET", "from-environment");
        try
        {
            Assert.Equal("from-environment", MarshalConfig.ResolveSecret("env:MARSHAL_TEST_SECRET"));
            Assert.Null(MarshalConfig.ResolveSecret("env:MARSHAL_TEST_SECRET_MISSING"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARSHAL_TEST_SECRET", null);
        }
    }

    [Fact]
    public void SecretResolvesFromAFileReference()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "secret-from-file\n");
            Assert.Equal("secret-from-file", MarshalConfig.ResolveSecret($"file:{path}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private string WriteConfig(Action<Dictionary<string, object?>>? mutate = null, string[]? schedule = null, bool jobWebhook = false, bool webhookEnabled = false)
    {
        string fakeExe = Path.Combine(_directory.Path, "SlimShady.exe");
        File.WriteAllText(fakeExe, "stub");
        string fakeJobConfig = Path.Combine(_directory.Path, "slimshady.json");
        File.WriteAllText(fakeJobConfig, "{}");

        var job = new Dictionary<string, object?> { ["name"] = "job-a", ["slimShadyConfigPath"] = fakeJobConfig };
        if (schedule is not null)
        {
            job["schedule"] = schedule;
        }

        if (jobWebhook)
        {
            job["webhook"] = new Dictionary<string, object?> { ["provider"] = "gitHub", ["repository"] = "mboler/SlimShady" };
        }

        var values = new Dictionary<string, object?> { ["slimShadyExecutable"] = fakeExe, ["jobs"] = new[] { job } };
        if (webhookEnabled)
        {
            values["webServer"] = new Dictionary<string, object?> { ["enabled"] = true, ["bindAddress"] = "localhost", ["port"] = 5000 };
            values["webhook"] = new Dictionary<string, object?> { ["enabled"] = true, ["gitHubSecret"] = "s" };
        }

        mutate?.Invoke(values);
        string path = Path.Combine(_directory.Path, "marshal.json");
        File.WriteAllText(path, JsonSerializer.Serialize(values));
        return path;
    }
}
