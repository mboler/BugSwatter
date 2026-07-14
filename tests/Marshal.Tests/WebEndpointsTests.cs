namespace Marshal.Tests;

public sealed class WebEndpointsTests
{
    [Fact]
    public void DashboardUsesTheProductNameAndExplainsTokenCounts()
    {
        Assert.Contains("<title>BugSwatter Dashboard</title>", DashboardPage.Html);
        Assert.Contains("<h1>BugSwatter Dashboard</h1>", DashboardPage.Html);
        Assert.Contains("Provider-reported tokens", DashboardPage.Html);
        Assert.DoesNotContain("Marshal dashboard", DashboardPage.Html);
    }

    [Fact]
    public async Task BodyWithinTheLimitIsReadCompletely()
    {
        byte[] payload = new byte[64 * 1024];
        new Random(7).NextBytes(payload);

        using var input = new MemoryStream(payload);
        byte[]? body = await WebEndpoints.ReadBodyBoundedAsync(input, 1024 * 1024);

        Assert.NotNull(body);
        Assert.Equal(payload, body);
    }

    [Fact]
    public async Task BodyBeyondTheLimitIsRejectedRegardlessOfHeaders()
    {
        // Simulates a chunked request that declared no Content-Length and streams past the cap
        using var input = new MemoryStream(new byte[2 * 1024 * 1024]);
        byte[]? body = await WebEndpoints.ReadBodyBoundedAsync(input, 1024 * 1024);

        Assert.Null(body);
    }

    [Fact]
    public async Task BodyExactlyAtTheLimitIsAccepted()
    {
        using var input = new MemoryStream(new byte[1024]);
        byte[]? body = await WebEndpoints.ReadBodyBoundedAsync(input, 1024);

        Assert.NotNull(body);
        Assert.Equal(1024, body.Length);
    }
}
