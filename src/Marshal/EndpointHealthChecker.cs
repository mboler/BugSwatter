using System.Text.Json;

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

/// <summary>Reads the model endpoint from a job's SlimShady config with a minimal comment-tolerant JSON read, the same lightweight peek used for report discovery, so Marshal can health-check without parsing the whole SlimShady config format</summary>
public static class JobConfigReader
{
    /// <summary>Returns the modelEndpoint value from the config, or null when it cannot be read</summary>
    public static string? TryReadModelEndpoint(string slimShadyConfigPath)
    {
        try
        {
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var document = JsonDocument.Parse(File.ReadAllText(slimShadyConfigPath), options);
            return document.RootElement.TryGetProperty("modelEndpoint", out JsonElement element) ? element.GetString() : null;
        }
        catch (Exception)
        {
            // catch-all: an unreadable config just means the health check is skipped and SlimShady itself reports the problem
            return null;
        }
    }
}
