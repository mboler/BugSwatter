namespace BugSwatter.Common;

/// <summary>Result of checking whether an OpenAI-compatible endpoint answers its models route</summary>
public sealed record ModelEndpointProbeResult(bool Reachable, int? StatusCode, string? Error);

/// <summary>Performs the model-endpoint reachability check shared by validation and dispatch</summary>
public static class ModelEndpointProbe
{
    /// <summary>Returns whether the endpoint answered within the timeout; any HTTP status counts as reachable</summary>
    public static async Task<ModelEndpointProbeResult> CheckAsync(HttpClient http, string endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await http.GetAsync($"{endpoint.TrimEnd('/')}/models", timeoutSource.Token);
            return new ModelEndpointProbeResult(true, (int)response.StatusCode, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new ModelEndpointProbeResult(false, null, ex.Message);
        }
    }
}
