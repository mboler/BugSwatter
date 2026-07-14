using System.Text.Json;
using BugSwatter.Common;

namespace Marshal;

/// <summary>Checks whether a job's model endpoint is answering before Marshal launches a review against it</summary>
public interface IEndpointHealthChecker
{
    /// <summary>Returns true when the endpoint answers at all; any HTTP response counts, only a connection failure or timeout is a miss</summary>
    Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken);
}

/// <summary>HTTP health checker: a short-timeout GET against the endpoint's models route</summary>
public sealed class HttpEndpointHealthChecker : IEndpointHealthChecker
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;

    /// <summary>Creates a checker over the shared HttpClient</summary>
    public HttpEndpointHealthChecker(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        ModelEndpointProbeResult result = await ModelEndpointProbe.CheckAsync(_http, endpoint, ProbeTimeout, cancellationToken);
        return result.Reachable;
    }
}

/// <summary>Reads runtime values from a job's Informant config through the same JSON-plus-environment configuration stack Informant uses</summary>
public static class JobConfigReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class InformantRuntimeConfig
    {
        public string? ModelEndpoint { get; init; }

        public IReadOnlyList<FallbackRuntimeModel> FallbackModels { get; init; } = [];

        public string ReportDirectory { get; init; } = "reports";

        public string RepositoryUrl { get; init; } = "";

        public string Branch { get; init; } = "";

        public string GitExecutablePath { get; init; } = "git";

        public string StateFilePath { get; init; } = "informant.state.json";
    }

    private sealed class FallbackRuntimeModel
    {
        public string? Endpoint { get; init; }
    }

    private sealed record BaselineEntry(string Sha, DateTimeOffset UpdatedUtc);

    /// <summary>Returns the modelEndpoint value from the config, or null when it cannot be read</summary>
    public static string? TryReadModelEndpoint(string informantConfigPath) => TryLoad(informantConfigPath)?.ModelEndpoint;

    /// <summary>Returns the preferred endpoint followed by distinct configured fallback endpoints, or an empty list when the config cannot be read</summary>
    public static IReadOnlyList<string> TryReadModelEndpoints(string informantConfigPath)
    {
        InformantRuntimeConfig? config = TryLoad(informantConfigPath);
        if (config is null)
        {
            return [];
        }

        var endpoints = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.ModelEndpoint))
        {
            endpoints.Add(config.ModelEndpoint);
        }

        foreach (FallbackRuntimeModel fallback in config.FallbackModels ?? [])
        {
            if (!string.IsNullOrWhiteSpace(fallback.Endpoint) && !endpoints.Contains(fallback.Endpoint, StringComparer.OrdinalIgnoreCase))
            {
                endpoints.Add(fallback.Endpoint);
            }
        }

        return endpoints;
    }

    /// <summary>Returns the absolute report directory from the config, including environment overrides, or null when it cannot be read</summary>
    public static string? TryReadReportDirectory(string informantConfigPath)
    {
        InformantRuntimeConfig? config = TryLoad(informantConfigPath);
        return config is null ? null : ConfigLoader.ResolvePath(ConfigLoader.GetConfigDirectory(informantConfigPath), config.ReportDirectory);
    }

    /// <summary>Returns the effective repository polling target, including environment overrides and config-relative paths, or null when the config cannot be read or lacks required values</summary>
    public static RepositoryPollTarget? TryReadRepositoryPollTarget(string informantConfigPath)
    {
        InformantRuntimeConfig? config = TryLoad(informantConfigPath);
        if (config is null || string.IsNullOrWhiteSpace(config.RepositoryUrl) || string.IsNullOrWhiteSpace(config.Branch) || string.IsNullOrWhiteSpace(config.GitExecutablePath))
        {
            return null;
        }

        string directory = ConfigLoader.GetConfigDirectory(informantConfigPath);
        string gitPath = Path.IsPathFullyQualified(config.GitExecutablePath) || !config.GitExecutablePath.Contains(Path.DirectorySeparatorChar) && !config.GitExecutablePath.Contains(Path.AltDirectorySeparatorChar)
            ? config.GitExecutablePath
            : ConfigLoader.ResolvePath(directory, config.GitExecutablePath);
        string statePath = ConfigLoader.ResolvePath(directory, config.StateFilePath);
        return new RepositoryPollTarget(config.RepositoryUrl, config.Branch, gitPath, statePath);
    }

    /// <summary>Reads the last successfully reviewed SHA for a polling target, or null when no baseline has been recorded</summary>
    /// <exception cref="InvalidDataException">The existing state file cannot be read or parsed</exception>
    public static string? ReadBaselineSha(RepositoryPollTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!File.Exists(target.StateFilePath))
        {
            return null;
        }

        try
        {
            Dictionary<string, BaselineEntry> entries = JsonSerializer.Deserialize<Dictionary<string, BaselineEntry>>(File.ReadAllText(target.StateFilePath), JsonOptions) ?? [];
            return entries.TryGetValue($"{target.RepositoryUrl}|{target.Branch}", out BaselineEntry? entry) ? entry.Sha : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"State file {target.StateFilePath} could not be read: {ex.Message}", ex);
        }
    }

    private static InformantRuntimeConfig? TryLoad(string informantConfigPath)
    {
        try
        {
            return ConfigLoader.Load<InformantRuntimeConfig>(Path.GetFullPath(informantConfigPath), "INFORMANT_");
        }
        catch (Exception)
        {
            // catch-all: an unreadable config just means the health check or fallback report discovery is skipped and Informant itself reports the problem
            return null;
        }
    }
}

/// <summary>The effective repository, branch, Git executable and completed-review state used by one polling job</summary>
public sealed record RepositoryPollTarget(string RepositoryUrl, string Branch, string GitExecutablePath, string StateFilePath);
