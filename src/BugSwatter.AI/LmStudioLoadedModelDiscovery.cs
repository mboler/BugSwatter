using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugSwatter.Common;

namespace BugSwatter.AI;

/// <summary>Outcome of discovering the language model already loaded at an LM Studio endpoint</summary>
public enum LoadedModelDiscoveryStatus
{
    /// <summary>Exactly one loaded language model was found</summary>
    Available,

    /// <summary>No loaded language model was found</summary>
    NoneLoaded,

    /// <summary>More than one loaded language model was found</summary>
    Ambiguous,

    /// <summary>The configured URL is not an LM Studio-style endpoint</summary>
    Unsupported,

    /// <summary>The endpoint did not return usable model metadata</summary>
    Unavailable
}

/// <summary>Loaded-model discovery result with the resolved API identifier when unambiguous</summary>
public sealed record LoadedModelDiscoveryResult(LoadedModelDiscoveryStatus Status, string? ModelName, IReadOnlyList<string> LoadedModelNames, string Detail);

/// <summary>Discovers one already-loaded LM Studio language model without loading or unloading anything</summary>
public static class LmStudioLoadedModelDiscovery
{
    private const int MaxMetadataBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns the single loaded language-model identifier, or a clear non-success result</summary>
    public static async Task<LoadedModelDiscoveryResult> DiscoverAsync(HttpClient http, string endpoint, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (!TryBuildModelsUris(endpoint, out Uri? versionOneUri, out Uri? versionZeroUri))
        {
            return Result(LoadedModelDiscoveryStatus.Unsupported, [], "loaded-model selection requires an LM Studio-style /v1 endpoint");
        }

        ModelListQueryResult versionOne = await QueryVersionOneAsync(http, versionOneUri!, timeout, cancellationToken).ConfigureAwait(false);
        LoadedModelDiscoveryResult? versionOneResult = Evaluate(versionOne.ModelNames, "LM Studio v1");
        if (versionOneResult is { Status: LoadedModelDiscoveryStatus.Available or LoadedModelDiscoveryStatus.Ambiguous })
        {
            return versionOneResult;
        }

        ModelListQueryResult versionZero = await QueryVersionZeroAsync(http, versionZeroUri!, timeout, cancellationToken).ConfigureAwait(false);
        LoadedModelDiscoveryResult? versionZeroResult = Evaluate(versionZero.ModelNames, "LM Studio v0 compatibility metadata");
        if (versionZeroResult is { Status: LoadedModelDiscoveryStatus.Available })
        {
            string detail = versionOne.Succeeded
                ? $"{versionZeroResult.Detail}; LM Studio v1 reported no loaded instances"
                : $"{versionZeroResult.Detail}; LM Studio v1 was unavailable: {versionOne.Detail}";
            return versionZeroResult with { Detail = detail };
        }

        if (versionZeroResult is { Status: LoadedModelDiscoveryStatus.Ambiguous })
        {
            return versionZeroResult;
        }

        if (versionZero.Succeeded)
        {
            return Result(LoadedModelDiscoveryStatus.NoneLoaded, [], "LM Studio reports no loaded language model");
        }

        return Result(LoadedModelDiscoveryStatus.Unavailable, [], $"LM Studio model metadata was unavailable: v1: {versionOne.Detail}; v0: {versionZero.Detail}");
    }

    private static async Task<ModelListQueryResult> QueryVersionOneAsync(HttpClient http, Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        PayloadQueryResult<VersionOneModelsResponse> query = await QueryAsync<VersionOneModelsResponse>(http, uri, timeout, cancellationToken).ConfigureAwait(false);
        if (query.Payload?.Models is null)
        {
            return new ModelListQueryResult(false, [], query.Detail);
        }

        string[] modelNames =
        [
            .. query.Payload.Models
                .Where(model => IsLanguageModelType(model.Type))
                .SelectMany(model => model.LoadedInstances ?? [])
                .Select(instance => instance.Id)
                .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
                .Select(modelName => modelName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
        return new ModelListQueryResult(true, modelNames, "LM Studio v1 model metadata is available");
    }

    private static async Task<ModelListQueryResult> QueryVersionZeroAsync(HttpClient http, Uri uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        PayloadQueryResult<VersionZeroModelsResponse> query = await QueryAsync<VersionZeroModelsResponse>(http, uri, timeout, cancellationToken).ConfigureAwait(false);
        if (query.Payload?.Data is null)
        {
            return new ModelListQueryResult(false, [], query.Detail);
        }

        string[] modelNames =
        [
            .. query.Payload.Data
                .Where(model => string.Equals(model.State, "loaded", StringComparison.OrdinalIgnoreCase) && IsLanguageModelType(model.Type))
                .Select(model => model.Id)
                .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
                .Select(modelName => modelName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
        ];
        return new ModelListQueryResult(true, modelNames, "LM Studio v0 model metadata is available");
    }

    private static async Task<PayloadQueryResult<T>> QueryAsync<T>(HttpClient http, Uri uri, TimeSpan timeout, CancellationToken cancellationToken) where T : class
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxMetadataBytes)
            {
                return new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} exceeded {MaxMetadataBytes:N0} bytes");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
            byte[]? bytes = await BoundedStreamReader.ReadAsync(stream, MaxMetadataBytes, timeoutSource.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} exceeded {MaxMetadataBytes:N0} bytes");
            }

            T? payload = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
            return payload is null
                ? new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} returned an empty JSON value")
                : new PayloadQueryResult<T>(payload, $"{uri.AbsolutePath} returned model metadata");
        }
        catch (JsonException ex)
        {
            return new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} returned invalid JSON: {ex.Message}");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new PayloadQueryResult<T>(null, $"{uri.AbsolutePath} did not complete: {ex.Message}");
        }
    }

    private static LoadedModelDiscoveryResult? Evaluate(IReadOnlyList<string> modelNames, string source) => modelNames.Count switch
    {
        0 => null,
        1 => new LoadedModelDiscoveryResult(LoadedModelDiscoveryStatus.Available, modelNames[0], modelNames, $"{source} reports loaded model '{modelNames[0]}'"),
        _ => new LoadedModelDiscoveryResult(LoadedModelDiscoveryStatus.Ambiguous, null, modelNames, $"{source} reports multiple loaded language models: {string.Join(", ", modelNames)}")
    };

    private static bool IsLanguageModelType(string? type) =>
        string.Equals(type, "llm", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "vlm", StringComparison.OrdinalIgnoreCase);

    private static bool TryBuildModelsUris(string endpoint, out Uri? versionOneUri, out Uri? versionZeroUri)
    {
        versionOneUri = null;
        versionZeroUri = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri) || !string.Equals(endpointUri.AbsolutePath.TrimEnd('/'), "/v1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        versionOneUri = BuildUri(endpointUri, "/api/v1/models");
        versionZeroUri = BuildUri(endpointUri, "/api/v0/models");
        return true;
    }

    private static Uri BuildUri(Uri endpoint, string path) => new UriBuilder(endpoint) { Path = path, Query = "", Fragment = "" }.Uri;

    private static LoadedModelDiscoveryResult Result(LoadedModelDiscoveryStatus status, IReadOnlyList<string> modelNames, string detail) => new(status, null, modelNames, detail);

    private sealed record PayloadQueryResult<T>(T? Payload, string Detail) where T : class;

    private sealed record ModelListQueryResult(bool Succeeded, IReadOnlyList<string> ModelNames, string Detail);

    private sealed class VersionOneModelsResponse
    {
        [JsonPropertyName("models")]
        public List<VersionOneModel>? Models { get; init; }
    }

    private sealed class VersionOneModel
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("loaded_instances")]
        public List<VersionOneLoadedInstance>? LoadedInstances { get; init; }
    }

    private sealed class VersionOneLoadedInstance
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }

    private sealed class VersionZeroModelsResponse
    {
        [JsonPropertyName("data")]
        public List<VersionZeroModel>? Data { get; init; }
    }

    private sealed class VersionZeroModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }
}
