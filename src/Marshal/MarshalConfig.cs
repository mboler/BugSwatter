using System.Text.Json.Serialization;
using BugSwatter.Common;

namespace Marshal;

/// <summary>Webhook provider a job listens for</summary>
public enum WebhookProvider
{
    /// <summary>GitHub push webhooks, validated with the X-Hub-Signature-256 HMAC header</summary>
    GitHub,

    /// <summary>Azure DevOps service hooks, validated with basic authentication credentials</summary>
    AzureDevOps
}

/// <summary>Webhook enablement for one job: which provider posts for it and which repository identifier in the payload maps to it</summary>
public sealed record JobWebhookConfig
{
    /// <summary>The provider whose webhook triggers this job</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<WebhookProvider>))]
    public WebhookProvider Provider { get; init; } = WebhookProvider.GitHub;

    /// <summary>Repository identifier expected in the payload: the full name for GitHub (owner/repo), the repository name or remote URL for Azure DevOps</summary>
    public string Repository { get; init; } = "";
}

/// <summary>One repository Marshal watches: where its Informant config lives and which triggers enqueue it</summary>
public sealed record ReviewJobConfig
{
    private string _configDirectory = Directory.GetCurrentDirectory();
    private bool _pathsResolved;
    private string _informantConfigPath = "";
    private string? _watchPath;

    /// <summary>Display name used in logs</summary>
    public string Name { get; init; } = "";

    /// <summary>Path of the Informant config for this repository, resolved from the Marshal configuration directory when relative; Marshal passes it to Informant via --config</summary>
    public string InformantConfigPath
    {
        get => _pathsResolved ? ConfigLoader.ResolvePath(_configDirectory, _informantConfigPath) : _informantConfigPath;
        init => _informantConfigPath = value;
    }

    /// <summary>Daily local times (HH:mm) at which the job is enqueued; null or empty for no schedule</summary>
    public IReadOnlyList<string>? Schedule { get; init; }

    /// <summary>Directory watched for file changes, resolved from the Marshal configuration directory when relative; null for no filesystem trigger</summary>
    public string? WatchPath
    {
        get => string.IsNullOrWhiteSpace(_watchPath) || !_pathsResolved ? _watchPath : ConfigLoader.ResolvePath(_configDirectory, _watchPath);
        init => _watchPath = value;
    }

    /// <summary>Webhook mapping; null when webhooks do not trigger this job</summary>
    public JobWebhookConfig? Webhook { get; init; }

    internal void SetConfigDirectory(string configDirectory)
    {
        _configDirectory = configDirectory;
        _pathsResolved = true;
    }
}

/// <summary>Kestrel web server settings: one listener serving the health, status and dashboard routes always, and the webhook routes when webhooks are enabled. Bind this to an internal or VPN-reachable interface only; the dashboard exposes repository names and findings</summary>
public sealed record WebServerSettings
{
    /// <summary>Whether the web server runs at all; null block means no web server</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Address the listener binds to; keep this internal or VPN-reachable, never a public interface</summary>
    public string BindAddress { get; init; } = "localhost";

    /// <summary>Listener port</summary>
    public int Port { get; init; } = 5000;
}

/// <summary>Webhook settings: enablement and the provider secrets. The listener itself is the shared web server, so binding lives in the webServer block</summary>
public sealed record WebhookSettings
{
    /// <summary>Whether webhook routes are served</summary>
    public bool Enabled { get; init; }

    /// <summary>GitHub shared secret for HMAC validation; a literal value, env:VARIABLE_NAME, or file:PATH</summary>
    public string? GitHubSecret { get; init; }

    /// <summary>Azure DevOps basic-auth password; a literal value, env:VARIABLE_NAME, or file:PATH</summary>
    public string? AzureDevOpsSecret { get; init; }
}

/// <summary>Marshal configuration: the Informant executable it dispatches, global dispatch settings, and the jobs it watches</summary>
public sealed class MarshalConfig
{
    private string _configDirectory = Directory.GetCurrentDirectory();
    private bool _pathsResolved;
    private string _informantExecutable = "";
    private string _logFilePath = "logs/marshal-.log";
    private string _historyFilePath = "history/marshal-history.jsonl";

    /// <summary>Path of the Informant executable Marshal launches for each review, resolved from the Marshal configuration directory when relative</summary>
    public string InformantExecutable
    {
        get => ResolvePath(_informantExecutable);
        init => _informantExecutable = value;
    }

    /// <summary>Hard timeout per Informant run; a child exceeding it is killed and the run marked failed</summary>
    public int PerRunTimeoutMinutes { get; init; } = 360;

    /// <summary>Quiet window after the last file change before a watched job is enqueued</summary>
    public int FileWatchDebounceSeconds { get; init; } = 300;

    /// <summary>Minimum Serilog level: Verbose, Debug, Information, Warning, Error or Fatal</summary>
    public string LogLevel { get; init; } = "Information";

    /// <summary>Log file path resolved from the Marshal configuration directory when relative; a rolling file per day is written next to it</summary>
    public string LogFilePath
    {
        get => ResolvePath(_logFilePath);
        init => _logFilePath = value;
    }

    /// <summary>Append-only JSON-lines file recording completed runs, resolved from the Marshal configuration directory when relative and read by the dashboard and status views</summary>
    public string HistoryFilePath
    {
        get => ResolvePath(_historyFilePath);
        init => _historyFilePath = value;
    }

    /// <summary>Forces the console sink on (true) or off (false); null auto-detects interactivity</summary>
    public bool? ConsoleLogging { get; init; }

    /// <summary>Web server settings; null means no web server (no dashboard, health or webhooks)</summary>
    public WebServerSettings? WebServer { get; init; }

    /// <summary>Webhook settings; requires the web server to be enabled</summary>
    public WebhookSettings? Webhook { get; init; }

    /// <summary>The repositories Marshal watches; zero or more</summary>
    public IReadOnlyList<ReviewJobConfig> Jobs { get; init; } = [];

    /// <summary>Loads and validates the configuration from an explicit file path</summary>
    public static MarshalConfig Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new MarshalFatalException($"Marshal config file not found: {fullPath}");
        }

        MarshalConfig config;
        try
        {
            config = ConfigLoader.Load<MarshalConfig>(fullPath, "MARSHAL_");
            config.SetConfigDirectory(ConfigLoader.GetConfigDirectory(fullPath));
        }
        catch (Exception ex) when (ex is not MarshalFatalException)
        {
            // catch-all: a malformed JSON file or a value that cannot bind to the config shape surfaces as a fatal config error
            throw new MarshalFatalException($"Marshal config {fullPath} could not be read: {ex.Message}", ex);
        }

        config.Validate();
        return config;
    }

    /// <summary>Resolves a secret value that is either a literal or an env:VARIABLE_NAME reference</summary>
    public static string? ResolveSecret(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        // Marshal's webhook secrets may be literals as well as env:/file: references; the shared resolver handles the reference forms
        return SecretReference.IsReference(configured) ? SecretReference.Resolve(configured) : configured;
    }

    /// <summary>Resolves a configured webhook secret, anchoring file references to the Marshal configuration directory</summary>
    public string? ResolveConfiguredSecret(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        return SecretReference.IsReference(configured) ? SecretReference.Resolve(configured, _configDirectory) : configured;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(InformantExecutable))
        {
            throw new MarshalFatalException("Config field 'informantExecutable' is required");
        }

        if (!File.Exists(InformantExecutable))
        {
            throw new MarshalFatalException($"informantExecutable does not exist: {InformantExecutable}");
        }

        if (PerRunTimeoutMinutes <= 0)
        {
            throw new MarshalFatalException($"perRunTimeoutMinutes must be greater than zero, got {PerRunTimeoutMinutes}");
        }

        if (FileWatchDebounceSeconds <= 0)
        {
            throw new MarshalFatalException($"fileWatchDebounceSeconds must be greater than zero, got {FileWatchDebounceSeconds}");
        }

        if (WebServer is { Enabled: true } webServer && webServer.Port is < 1 or > 65535)
        {
            throw new MarshalFatalException($"webServer.port must be between 1 and 65535, got {webServer.Port}");
        }

        if (Webhook is { Enabled: true } webhook)
        {
            if (WebServer is not { Enabled: true })
            {
                throw new MarshalFatalException("webhook is enabled but the webServer block is not; webhooks are served on the web server, so enable webServer or disable webhook");
            }

            ValidateSecretReference(webhook.GitHubSecret, "webhook.gitHubSecret");
            ValidateSecretReference(webhook.AzureDevOpsSecret, "webhook.azureDevOpsSecret");
        }

        foreach (var job in Jobs)
        {
            ValidateJob(job);
        }
    }

    private void SetConfigDirectory(string configDirectory)
    {
        _configDirectory = configDirectory;
        _pathsResolved = true;
        foreach (ReviewJobConfig job in Jobs)
        {
            job.SetConfigDirectory(configDirectory);
        }
    }

    private string ResolvePath(string configuredPath) => _pathsResolved ? ConfigLoader.ResolvePath(_configDirectory, configuredPath) : configuredPath;

    private static void ValidateSecretReference(string? value, string fieldName)
    {
        // A bare env: or file: prefix would silently resolve to no secret at runtime; catch the typo at load instead
        if (value is not null && value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(value[4..]))
        {
            throw new MarshalFatalException($"{fieldName} names an environment reference with no variable name; use env:VARIABLE_NAME");
        }

        if (value is not null && value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(value[5..]))
        {
            throw new MarshalFatalException($"{fieldName} names a file reference with no path; use file:PATH");
        }
    }

    private void ValidateJob(ReviewJobConfig job)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            throw new MarshalFatalException("Every job needs a non-empty 'name'");
        }

        if (string.IsNullOrWhiteSpace(job.InformantConfigPath))
        {
            throw new MarshalFatalException($"Job '{job.Name}' needs a 'informantConfigPath'");
        }

        if (!File.Exists(job.InformantConfigPath))
        {
            throw new MarshalFatalException($"Job '{job.Name}': Informant config not found at {job.InformantConfigPath}");
        }

        foreach (string time in job.Schedule ?? [])
        {
            if (!TimeOnly.TryParse(time, out _))
            {
                throw new MarshalFatalException($"Job '{job.Name}': schedule entry '{time}' is not a valid time of day (expected HH:mm)");
            }
        }

        if (job.WatchPath is not null && !Directory.Exists(job.WatchPath))
        {
            throw new MarshalFatalException($"Job '{job.Name}': watchPath does not exist: {job.WatchPath}");
        }

        if (job.Webhook is not null && (Webhook is not { Enabled: true }))
        {
            throw new MarshalFatalException($"Job '{job.Name}' maps a webhook but the global webhook listener is not enabled");
        }

        if (job.Webhook is not null && string.IsNullOrWhiteSpace(job.Webhook.Repository))
        {
            throw new MarshalFatalException($"Job '{job.Name}': webhook mapping needs a 'repository' identifier");
        }
    }
}
