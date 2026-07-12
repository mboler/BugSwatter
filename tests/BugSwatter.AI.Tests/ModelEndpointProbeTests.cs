using System.Net;

namespace BugSwatter.AI.Tests;

public sealed class ModelEndpointProbeTests
{
    [Fact]
    public async Task AnyHttpResponseIsReachableAndUsesModelsRoute()
    {
        var handler = new ProbeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);

        ModelEndpointProbeResult result = await ModelEndpointProbe.CheckAsync(http, "https://example.test/v1/", TimeSpan.FromSeconds(1));

        Assert.True(result.Reachable);
        Assert.Equal(401, result.StatusCode);
        Assert.Equal("https://example.test/v1/models", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ConnectionFailureIsNotReachable()
    {
        var handler = new ProbeHandler((_, _) => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler);

        ModelEndpointProbeResult result = await ModelEndpointProbe.CheckAsync(http, "https://example.test/v1", TimeSpan.FromSeconds(1));

        Assert.False(result.Reachable);
        Assert.Null(result.StatusCode);
        Assert.Contains("offline", result.Error);
    }

    [Fact]
    public async Task TimeoutIsNotReachable()
    {
        var handler = new ProbeHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);

        ModelEndpointProbeResult result = await ModelEndpointProbe.CheckAsync(http, "https://example.test/v1", TimeSpan.FromMilliseconds(20));

        Assert.False(result.Reachable);
    }

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return send(request, cancellationToken);
        }
    }
}
