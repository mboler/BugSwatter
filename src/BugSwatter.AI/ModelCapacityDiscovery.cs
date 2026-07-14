using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugSwatter.Common;

namespace BugSwatter.AI;

/// <summary>Availability and consistency state of optional model-capacity metadata</summary>
public enum ModelCapacityMetadataStatus
{
    /// <summary>Usable loaded or maximum context metadata was found</summary>
    Available,

    /// <summary>The endpoint does not expose recognizable LM Studio metadata for the configured model</summary>
    Unavailable,

    /// <summary>The metadata response could not be safely parsed</summary>
    Malformed,

    /// <summary>The parsed metadata contains impossible or conflicting context values</summary>
    Contradictory
}

/// <summary>Best-effort context-capacity metadata for one configured model</summary>
public sealed record ModelCapacityMetadataResult(ModelCapacityMetadataStatus Status, int? LoadedContextTokens, int? MaximumContextTokens, string Detail);

/// <summary>Discovers LM Studio native model metadata without changing or loading a model</summary>
public static class ModelCapacityDiscovery
{
    private const int MaxMetadataBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Queries the LM Studio native models route derived from an OpenAI-compatible endpoint</summary>
    public static async Task<ModelCapacityMetadataResult> DiscoverLmStudioAsync(HttpClient http, string endpoint, string modelName, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (!TryBuildNativeModelsUri(endpoint, out Uri? modelsUri))
        {
            return Unavailable("endpoint is not an LM Studio-style /v1 base URL");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable($"LM Studio native models route answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
            {
                return Malformed($"metadata response exceeded {MaxMetadataBytes:N0} bytes");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            byte[]? bytes = await BoundedStreamReader.ReadAsync(stream, MaxMetadataBytes, timeoutSource.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return Malformed($"metadata response exceeded {MaxMetadataBytes:N0} bytes");
            }

            return Parse(bytes, modelName);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return Unavailable($"LM Studio native metadata request did not complete: {ex.Message}");
        }
    }

    private static ModelCapacityMetadataResult Parse(byte[] bytes, string modelName)
    {
        LmStudioModelsResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LmStudioModelsResponse>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Malformed($"metadata response was not valid JSON: {ex.Message}");
        }

        if (payload?.Models is null)
        {
            return Malformed("metadata response did not contain a models array");
        }

        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        LmStudioModel? model = payload.Models.FirstOrDefault(candidate => candidate.LoadedInstances?.Any(instance => string.Equals(instance.Id, modelName, comparison)) == true)
            ?? payload.Models.FirstOrDefault(candidate => string.Equals(candidate.Key, modelName, comparison));
        if (model is null)
        {
            return Unavailable($"metadata did not contain configured model '{modelName}'");
        }

        LmStudioLoadedInstance? loadedInstance = model.LoadedInstances?.FirstOrDefault(instance => string.Equals(instance.Id, modelName, comparison));
        if (loadedInstance is null && string.Equals(model.Key, modelName, comparison) && model.LoadedInstances is { Count: 1 })
        {
            loadedInstance = model.LoadedInstances[0];
        }

        int? loadedContextTokens = loadedInstance?.Config?.ContextLength;
        int? maximumContextTokens = model.MaximumContextLength;
        if (loadedContextTokens is <= 0 || maximumContextTokens is <= 0)
        {
            return Contradictory(loadedContextTokens, maximumContextTokens, "context lengths must be greater than zero");
        }

        if (loadedContextTokens.HasValue && maximumContextTokens.HasValue && loadedContextTokens > maximumContextTokens)
        {
            return Contradictory(loadedContextTokens, maximumContextTokens, "loaded context is larger than the model maximum");
        }

        if (!loadedContextTokens.HasValue && !maximumContextTokens.HasValue)
        {
            return Unavailable($"metadata for configured model '{modelName}' did not include context lengths");
        }

        return new ModelCapacityMetadataResult(ModelCapacityMetadataStatus.Available, loadedContextTokens, maximumContextTokens, "LM Studio context metadata is available");
    }

    private static bool TryBuildNativeModelsUri(string endpoint, out Uri? modelsUri)
    {
        modelsUri = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) || !string.Equals(endpointUri.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        modelsUri = new UriBuilder(endpointUri)
        {
            Path = "/api/v1/models",
            Query = "",
            Fragment = ""
        }.Uri;
        return true;
    }

    private static ModelCapacityMetadataResult Unavailable(string detail) => new(ModelCapacityMetadataStatus.Unavailable, null, null, detail);

    private static ModelCapacityMetadataResult Malformed(string detail) => new(ModelCapacityMetadataStatus.Malformed, null, null, detail);

    private static ModelCapacityMetadataResult Contradictory(int? loadedContextTokens, int? maximumContextTokens, string detail) =>
        new(ModelCapacityMetadataStatus.Contradictory, loadedContextTokens, maximumContextTokens, detail);

    private sealed class LmStudioModelsResponse
    {
        [JsonPropertyName("models")]
        public List<LmStudioModel>? Models { get; init; }
    }

    private sealed class LmStudioModel
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("loaded_instances")]
        public List<LmStudioLoadedInstance>? LoadedInstances { get; init; }

        [JsonPropertyName("max_context_length")]
        public int? MaximumContextLength { get; init; }
    }

    private sealed class LmStudioLoadedInstance
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("config")]
        public LmStudioLoadedInstanceConfig? Config { get; init; }
    }

    private sealed class LmStudioLoadedInstanceConfig
    {
        [JsonPropertyName("context_length")]
        public int? ContextLength { get; init; }
    }
}
