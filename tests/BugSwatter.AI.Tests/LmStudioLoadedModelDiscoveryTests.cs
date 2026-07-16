using System.Net;

namespace BugSwatter.AI.Tests;

/// <summary>Verifies loaded-model discovery across LM Studio metadata versions</summary>
public sealed class LmStudioLoadedModelDiscoveryTests
{
    /// <summary>Verifies one native v1 loaded model resolves without compatibility fallback</summary>
    [Fact]
    public async Task ResolvesSingleVersionOneLoadedModel()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, VersionOneJson("review-model"));
        using var http = new HttpClient(handler);

        LoadedModelDiscoveryResult result = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, "http://model-host:1234/v1", TimeSpan.FromSeconds(1));

        Assert.Equal(LoadedModelDiscoveryStatus.Available, result.Status);
        Assert.Equal("review-model", result.ModelName);
        Assert.Equal("http://model-host:1234/api/v1/models", Assert.Single(handler.RequestUris)?.ToString());
    }

    /// <summary>Verifies empty v1 loaded instances use v0 loaded-state metadata</summary>
    [Fact]
    public async Task FallsBackToVersionZeroWhenVersionOneReportsNoLoadedInstances()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, VersionOneJson());
        handler.Enqueue(HttpStatusCode.OK, VersionZeroJson(("loaded-model", "loaded"), ("other-model", "not-loaded")));
        using var http = new HttpClient(handler);

        LoadedModelDiscoveryResult result = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, "http://model-host:1234/v1", TimeSpan.FromSeconds(1));

        Assert.Equal(LoadedModelDiscoveryStatus.Available, result.Status);
        Assert.Equal("loaded-model", result.ModelName);
        Assert.Contains("v1 reported no loaded instances", result.Detail);
        Assert.Equal(["http://model-host:1234/api/v1/models", "http://model-host:1234/api/v0/models"], handler.RequestUris.Select(uri => uri?.ToString()));
    }

    /// <summary>Verifies agreement on no loaded models returns a non-success result</summary>
    [Fact]
    public async Task ReportsNoLoadedModelWhenBothRoutesAgree()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, VersionOneJson());
        handler.Enqueue(HttpStatusCode.OK, VersionZeroJson(("other-model", "not-loaded")));
        using var http = new HttpClient(handler);

        LoadedModelDiscoveryResult result = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, "http://model-host:1234/v1", TimeSpan.FromSeconds(1));

        Assert.Equal(LoadedModelDiscoveryStatus.NoneLoaded, result.Status);
        Assert.Null(result.ModelName);
        Assert.Empty(result.LoadedModelNames);
    }

    /// <summary>Verifies multiple loaded models are rejected deterministically</summary>
    [Fact]
    public async Task RejectsMultipleLoadedModelsWithoutGuessing()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, VersionOneJson("model-b", "model-a"));
        using var http = new HttpClient(handler);

        LoadedModelDiscoveryResult result = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, "http://model-host:1234/v1", TimeSpan.FromSeconds(1));

        Assert.Equal(LoadedModelDiscoveryStatus.Ambiguous, result.Status);
        Assert.Null(result.ModelName);
        Assert.Equal(["model-a", "model-b"], result.LoadedModelNames);
        Assert.Single(handler.RequestUris);
    }

    /// <summary>Verifies malformed v1 metadata can fall back to usable v0 metadata</summary>
    [Fact]
    public async Task UsesVersionZeroWhenVersionOneMetadataIsMalformed()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{not-json");
        handler.Enqueue(HttpStatusCode.OK, VersionZeroJson(("loaded-model", "loaded")));
        using var http = new HttpClient(handler);

        LoadedModelDiscoveryResult result = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, "http://model-host:1234/v1", TimeSpan.FromSeconds(1));

        Assert.Equal(LoadedModelDiscoveryStatus.Available, result.Status);
        Assert.Equal("loaded-model", result.ModelName);
        Assert.Contains("v1 was unavailable", result.Detail);
    }

    /// <summary>Verifies wildcard discovery rejects non-LM Studio endpoint paths without I/O</summary>
    [Fact]
    public async Task RejectsNonLmStudioEndpointShapeWithoutSendingARequest()
    {
        var handler = new StubHttpMessageHandler();
        using var http = new HttpClient(handler);

        LoadedModelDiscoveryResult result = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, "https://provider.example/openai/v1", TimeSpan.FromSeconds(1));

        Assert.Equal(LoadedModelDiscoveryStatus.Unsupported, result.Status);
        Assert.Empty(handler.RequestUris);
    }

    private static string VersionOneJson(params string[] loadedModelNames)
    {
        string instances = string.Join(",", loadedModelNames.Select(modelName => $$"""{"id":"{{modelName}}"}"""));
        return $$"""
            {
              "models": [
                {
                  "type": "llm",
                  "loaded_instances": [{{instances}}]
                }
              ]
            }
            """;
    }

    private static string VersionZeroJson(params (string ModelName, string State)[] models)
    {
        string entries = string.Join(",", models.Select(model => $$"""{"id":"{{model.ModelName}}","type":"llm","state":"{{model.State}}"}"""));
        return $$"""
            {
              "data": [{{entries}}]
            }
            """;
    }
}
