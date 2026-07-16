using System.Net;

namespace Informant.Tests;

/// <summary>Verifies explicit and loaded-model primary target resolution</summary>
public sealed class PrimaryModelTargetResolverTests
{
    /// <summary>Verifies explicit model names do not perform discovery</summary>
    [Fact]
    public async Task ExplicitModelNameIsReturnedWithoutDiscovery()
    {
        var handler = new StubHttpMessageHandler();
        using var http = new HttpClient(handler);
        var target = new PrimaryModelTarget("primary", "https://provider.example/v1", "review-model", false);

        PrimaryModelTargetResolution resolution = await PrimaryModelTargetResolver.ResolveAsync(http, target, TimeSpan.FromSeconds(1));

        Assert.True(resolution.Succeeded);
        Assert.Same(target, resolution.Target);
        Assert.Empty(handler.RequestUris);
    }

    /// <summary>Verifies the wildcard is replaced with the discovered API identifier</summary>
    [Fact]
    public async Task WildcardIsReplacedWithLoadedModelIdentifier()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "models": [
                {
                  "type": "llm",
                  "loaded_instances": [
                    { "id": "loaded-review-model" }
                  ]
                }
              ]
            }
            """);
        using var http = new HttpClient(handler);
        var target = new PrimaryModelTarget("primary", "http://model-host:1234/v1", PrimaryModelTargetResolver.LoadedModelWildcard, false);

        PrimaryModelTargetResolution resolution = await PrimaryModelTargetResolver.ResolveAsync(http, target, TimeSpan.FromSeconds(1));

        Assert.True(resolution.Succeeded);
        Assert.Equal("loaded-review-model", resolution.Target.ModelName);
        Assert.Equal("primary", resolution.Target.Name);
    }

    /// <summary>Verifies no loaded model produces a clear non-success resolution</summary>
    [Fact]
    public async Task WildcardFailsClearlyWhenNoModelIsLoaded()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"models":[]}""");
        handler.Enqueue(HttpStatusCode.OK, """{"data":[]}""");
        using var http = new HttpClient(handler);
        var target = new PrimaryModelTarget("backup", "http://model-host:1234/v1", PrimaryModelTargetResolver.LoadedModelWildcard, true);

        PrimaryModelTargetResolution resolution = await PrimaryModelTargetResolver.ResolveAsync(http, target, TimeSpan.FromSeconds(1));

        Assert.False(resolution.Succeeded);
        Assert.Equal(PrimaryModelTargetResolver.LoadedModelWildcard, resolution.Target.ModelName);
        Assert.Contains("no loaded language model", resolution.Detail);
    }
}
