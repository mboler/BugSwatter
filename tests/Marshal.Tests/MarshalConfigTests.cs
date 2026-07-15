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
        Assert.Null(job.Poll);
    }

    [Fact]
    public void MissingFileIsFatal()
    {
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(Path.Combine(_directory.Path, "nope.json")));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void MissingInformantExecutableIsFatal()
    {
        string path = WriteConfig(mutate: values => values["informantExecutable"] = Path.Combine(_directory.Path, "ghost.exe"));
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("informantExecutable", ex.Message);
    }

    [Fact]
    public void InvalidScheduleTimeIsFatal()
    {
        string path = WriteConfig(schedule: ["25:99"]);
        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));
        Assert.Contains("25:99", ex.Message);
    }

    [Fact]
    public void PollBlockUsesFiveMinuteDefault()
    {
        string path = WriteConfig(mutate: values => ((Dictionary<string, object?>[])values["jobs"]!)[0]["poll"] = new Dictionary<string, object?> { ["enabled"] = true });

        MarshalConfig config = MarshalConfig.Load(path);

        Assert.Equal(RepositoryPollSettings.DefaultSchedule, Assert.Single(config.Jobs).Poll!.Schedule);
    }

    [Theory]
    [InlineData("0 * * * * *")]
    [InlineData("0 30 2 * * Mon-Fri")]
    [InlineData("00:01:00")]
    [InlineData("30.00:00:00")]
    public void ValidPollSchedulesLoad(string schedule)
    {
        string path = WriteConfig(mutate: values => ((Dictionary<string, object?>[])values["jobs"]!)[0]["poll"] = new Dictionary<string, object?> { ["schedule"] = schedule });

        Assert.Equal(schedule, MarshalConfig.Load(path).Jobs[0].Poll!.Schedule);
    }

    [Theory]
    [InlineData("*/30 * * * * *")]
    [InlineData("00:00:59")]
    [InlineData("invalid")]
    public void InvalidPollSchedulesAreFatal(string schedule)
    {
        string path = WriteConfig(mutate: values => ((Dictionary<string, object?>[])values["jobs"]!)[0]["poll"] = new Dictionary<string, object?> { ["schedule"] = schedule });

        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));

        Assert.Contains("poll.schedule", ex.Message);
    }

    [Fact]
    public void PollingWithoutRepositoryTargetIsFatal()
    {
        string path = WriteConfig(mutate: values =>
        {
            var jobs = (Dictionary<string, object?>[])values["jobs"]!;
            File.WriteAllText((string)jobs[0]["informantConfigPath"]!, "{}");
            jobs[0]["poll"] = new Dictionary<string, object?> { ["enabled"] = true };
        });

        MarshalFatalException ex = Assert.Throws<MarshalFatalException>(() => MarshalConfig.Load(path));

        Assert.Contains("requires repositoryUrl", ex.Message);
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

        string? originalTestSecret = Environment.GetEnvironmentVariable("MARSHAL_TEST_SECRET");
        Environment.SetEnvironmentVariable("MARSHAL_TEST_SECRET", "from-environment");
        try
        {
            Assert.Equal("from-environment", MarshalConfig.ResolveSecret("env:MARSHAL_TEST_SECRET"));
            Assert.Null(MarshalConfig.ResolveSecret("env:MARSHAL_TEST_SECRET_MISSING"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARSHAL_TEST_SECRET", originalTestSecret);
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

    [Fact]
    public void RelativePathsAndSecretFilesResolveAgainstConfigWithoutChangingCurrentDirectory()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "webhook-secret.txt"), "relative-secret\n");
        string path = WriteConfig(jobWebhook: true, webhookEnabled: true, mutate: values =>
        {
            values["informantExecutable"] = "Informant.exe";
            values["logFilePath"] = "logs/marshal-.log";
            values["historyFilePath"] = "history/runs.jsonl";
            values["webhook"] = new Dictionary<string, object?> { ["enabled"] = true, ["gitHubSecret"] = "file:webhook-secret.txt" };
            var jobs = (Dictionary<string, object?>[])values["jobs"]!;
            jobs[0]["informantConfigPath"] = "informant.json";
        });
        string originalDirectory = Directory.GetCurrentDirectory();

        MarshalConfig config = MarshalConfig.Load(path);

        Assert.Equal(originalDirectory, Directory.GetCurrentDirectory());
        Assert.Equal(Path.Combine(_directory.Path, "Informant.exe"), config.InformantExecutable);
        Assert.Equal(Path.Combine(_directory.Path, "logs", "marshal-.log"), config.LogFilePath);
        Assert.Equal(Path.Combine(_directory.Path, "history", "runs.jsonl"), config.HistoryFilePath);
        Assert.Equal(Path.Combine(_directory.Path, "informant.json"), Assert.Single(config.Jobs).InformantConfigPath);
        Assert.Equal("relative-secret", config.ResolveConfiguredSecret(config.Webhook!.GitHubSecret));
    }

    [Fact]
    public void EnvironmentVariablesOverrideTopLevelNestedAndArrayValues()
    {
        string alternateConfig = Path.Combine(_directory.Path, "alternate-informant.json");
        File.WriteAllText(alternateConfig, "{}");
        string path = WriteConfig(webhookEnabled: true);
        string? originalTimeout = Environment.GetEnvironmentVariable("MARSHAL_PerRunTimeoutMinutes");
        string? originalPort = Environment.GetEnvironmentVariable("MARSHAL_WebServer__Port");
        string? originalJobName = Environment.GetEnvironmentVariable("MARSHAL_Jobs__0__Name");
        string? originalConfigPath = Environment.GetEnvironmentVariable("MARSHAL_Jobs__0__InformantConfigPath");
        string? originalPollEnabled = Environment.GetEnvironmentVariable("MARSHAL_Jobs__0__Poll__Enabled");
        string? originalPollSchedule = Environment.GetEnvironmentVariable("MARSHAL_Jobs__0__Poll__Schedule");
        Environment.SetEnvironmentVariable("MARSHAL_PerRunTimeoutMinutes", "45");
        Environment.SetEnvironmentVariable("MARSHAL_WebServer__Port", "5055");
        Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__Name", "environment-job");
        Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__InformantConfigPath", alternateConfig);
        Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__Poll__Enabled", "true");
        Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__Poll__Schedule", "00:10:00");
        File.WriteAllText(alternateConfig, """{ "repositoryUrl": "https://example.test/repository.git", "branch": "main" }""");
        try
        {
            MarshalConfig config = MarshalConfig.Load(path);

            Assert.Equal(45, config.PerRunTimeoutMinutes);
            Assert.Equal(5055, config.WebServer!.Port);
            Assert.Equal("environment-job", Assert.Single(config.Jobs).Name);
            Assert.Equal(alternateConfig, config.Jobs[0].InformantConfigPath);
            Assert.Equal("00:10:00", config.Jobs[0].Poll!.Schedule);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MARSHAL_PerRunTimeoutMinutes", originalTimeout);
            Environment.SetEnvironmentVariable("MARSHAL_WebServer__Port", originalPort);
            Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__Name", originalJobName);
            Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__InformantConfigPath", originalConfigPath);
            Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__Poll__Enabled", originalPollEnabled);
            Environment.SetEnvironmentVariable("MARSHAL_Jobs__0__Poll__Schedule", originalPollSchedule);
        }
    }

    private string WriteConfig(Action<Dictionary<string, object?>>? mutate = null, string[]? schedule = null, bool jobWebhook = false, bool webhookEnabled = false)
    {
        string fakeExe = Path.Combine(_directory.Path, "Informant.exe");
        File.WriteAllText(fakeExe, "stub");
        string fakeJobConfig = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(fakeJobConfig, """{ "repositoryUrl": "https://example.test/repository.git", "branch": "main", "gitExecutablePath": "git" }""");

        var job = new Dictionary<string, object?> { ["name"] = "job-a", ["informantConfigPath"] = fakeJobConfig };
        if (schedule is not null)
        {
            job["schedule"] = schedule;
        }

        if (jobWebhook)
        {
            job["webhook"] = new Dictionary<string, object?> { ["provider"] = "gitHub", ["repository"] = "mboler/Informant" };
        }

        var values = new Dictionary<string, object?> { ["informantExecutable"] = fakeExe, ["jobs"] = new[] { job } };
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
