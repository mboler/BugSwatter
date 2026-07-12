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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync($"{endpoint.TrimEnd('/')}/models", timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Reads runtime values from a job's Informant config through the same JSON-plus-environment configuration stack Informant uses</summary>
public static class JobConfigReader
{
    private sealed class InformantRuntimeConfig
    {
        public string? ModelEndpoint { get; init; }

        public string ReportDirectory { get; init; } = "reports";
    }

    /// <summary>Returns the modelEndpoint value from the config, or null when it cannot be read</summary>
    public static string? TryReadModelEndpoint(string informantConfigPath) => TryLoad(informantConfigPath)?.ModelEndpoint;

    /// <summary>Returns the absolute report directory from the config, including environment overrides, or null when it cannot be read</summary>
    public static string? TryReadReportDirectory(string informantConfigPath)
    {
        InformantRuntimeConfig? config = TryLoad(informantConfigPath);
        return config is null ? null : ConfigLoader.ResolvePath(ConfigLoader.GetConfigDirectory(informantConfigPath), config.ReportDirectory);
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
