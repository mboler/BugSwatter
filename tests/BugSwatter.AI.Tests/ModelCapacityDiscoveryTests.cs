using System.Net;

namespace BugSwatter.AI.Tests;

public sealed class ModelCapacityDiscoveryTests
{
    [Fact]
    public async Task ReturnsLoadedAndMaximumContextForConfiguredInstance()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "models": [
                {
                  "type": "llm",
                  "key": "publisher/model",
                  "max_context_length": 262144,
                  "loaded_instances": [
                    {
                      "id": "review-model",
                      "config": { "context_length": 65536 }
                    }
                  ]
                }
              ]
            }
            """);
        using var http = new HttpClient(handler);

        ModelCapacityMetadataResult result = await ModelCapacityDiscovery.DiscoverLmStudioAsync(http, "http://model-host:1234/v1", "review-model", TimeSpan.FromSeconds(1));

        Assert.Equal(ModelCapacityMetadataStatus.Available, result.Status);
        Assert.Equal(65536, result.LoadedContextTokens);
        Assert.Equal(262144, result.MaximumContextTokens);
        Assert.Equal("http://model-host:1234/api/v1/models", Assert.Single(handler.RequestUris)?.ToString());
    }

    [Fact]
    public async Task MissingNativeMetadataIsUnavailable()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "{}");
        using var http = new HttpClient(handler);

        ModelCapacityMetadataResult result = await ModelCapacityDiscovery.DiscoverLmStudioAsync(http, "https://provider.example/v1", "review-model", TimeSpan.FromSeconds(1));

        Assert.Equal(ModelCapacityMetadataStatus.Unavailable, result.Status);
        Assert.Null(result.LoadedContextTokens);
        Assert.Null(result.MaximumContextTokens);
    }

    [Fact]
    public async Task ReturnsMaximumContextWhenConfiguredModelIsNotLoaded()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "models": [
                {
                  "type": "llm",
                  "key": "review-model",
                  "max_context_length": 131072,
                  "loaded_instances": []
                }
              ]
            }
            """);
        using var http = new HttpClient(handler);

        ModelCapacityMetadataResult result = await ModelCapacityDiscovery.DiscoverLmStudioAsync(http, "http://model-host:1234/v1", "review-model", TimeSpan.FromSeconds(1));

        Assert.Equal(ModelCapacityMetadataStatus.Available, result.Status);
        Assert.Null(result.LoadedContextTokens);
        Assert.Equal(131072, result.MaximumContextTokens);
    }

    [Fact]
    public async Task InvalidJsonIsMalformed()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{not-json");
        using var http = new HttpClient(handler);

        ModelCapacityMetadataResult result = await ModelCapacityDiscovery.DiscoverLmStudioAsync(http, "http://model-host:1234/v1", "review-model", TimeSpan.FromSeconds(1));

        Assert.Equal(ModelCapacityMetadataStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task LoadedContextLargerThanModelMaximumIsContradictory()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "models": [
                {
                  "type": "llm",
                  "key": "review-model",
                  "max_context_length": 32768,
                  "loaded_instances": [
                    {
                      "id": "review-model",
                      "config": { "context_length": 65536 }
                    }
                  ]
                }
              ]
            }
            """);
        using var http = new HttpClient(handler);

        ModelCapacityMetadataResult result = await ModelCapacityDiscovery.DiscoverLmStudioAsync(http, "http://model-host:1234/v1", "review-model", TimeSpan.FromSeconds(1));

        Assert.Equal(ModelCapacityMetadataStatus.Contradictory, result.Status);
        Assert.Equal(65536, result.LoadedContextTokens);
        Assert.Equal(32768, result.MaximumContextTokens);
    }
}
