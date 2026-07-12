using System.Net;
using System.Text.Json;

namespace Informant.Tests;

public sealed class ModelClientTests
{
    [Fact]
    public async Task SendsModelMessagesToolsAndToolChoice()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("hello"));
        ModelClient client = CreateClient(handler);
        ChatMessage reply = await client.CompleteAsync([new ChatMessage { Role = "system", Content = "sys" }, new ChatMessage { Role = "user", Content = "usr" }], [ReadFileLinesTool.Definition]);

        Assert.Equal("hello", reply.Content);
        using var request = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("test-model", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("sys", request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("auto", request.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(ReadFileLinesTool.ToolName, request.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("http://localhost:9999/v1/chat/completions", handler.RequestUris[0]!.ToString());
    }

    [Fact]
    public async Task ParsesToolCallReply()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("src/Foo.cs", 1, 10))));
        ChatMessage reply = await CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], [ReadFileLinesTool.Definition]);

        ToolCall call = Assert.Single(reply.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal(ReadFileLinesTool.ToolName, call.Function!.Name);
        Assert.Contains("src/Foo.cs", call.Function.Arguments);
    }

    [Fact]
    public async Task NonSuccessStatusThrowsModelCallException()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"error": "model exploded"}""");
        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));
        Assert.Contains("500", ex.Message);
        Assert.Contains("model exploded", ex.Message);
    }

    [Fact]
    public async Task UnparseableBodyThrowsModelCallException()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "this is not json");
        await Assert.ThrowsAsync<ModelCallException>(() => CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));
    }

    [Fact]
    public async Task EmptyChoicesThrowsModelCallException()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"choices": []}""");
        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));
        Assert.Contains("no message", ex.Message);
    }

    [Fact]
    public async Task ContentLengthOverResponseLimitIsRejected()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse(new string('x', 2000)));
        var client = new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5), maxResponseBytes: 1000);

        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => client.CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));

        Assert.Contains("maxModelResponseBytes", ex.Message);
    }

    [Fact]
    public async Task StreamingBodyOverResponseLimitIsRejectedWithoutContentLength()
    {
        var handler = new StubHttpMessageHandler();
        HttpContent content = StubHttpMessageHandler.UnknownLengthContent(StubHttpMessageHandler.FinalResponse(new string('x', 2000)));
        Assert.Null(content.Headers.ContentLength);
        handler.EnqueueContent(HttpStatusCode.OK, content);
        var client = new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5), maxResponseBytes: 1000);

        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => client.CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));

        Assert.Contains("maxModelResponseBytes", ex.Message);
    }

    [Fact]
    public async Task SlowResponseTimesOutAsModelCallException()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("too late"), TimeSpan.FromSeconds(30));
        var client = new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromMilliseconds(150));
        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => client.CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task ConnectionFailureThrowsModelCallException()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));
        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []));
        Assert.Contains("unreachable", ex.Message);
    }

    [Fact]
    public async Task TrailingEndpointSlashIsNormalized()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("ok"));
        var client = new ModelClient(new HttpClient(handler), "http://localhost:9999/v1/", "test-model", TimeSpan.FromSeconds(5));
        await client.CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []);
        Assert.Equal("http://localhost:9999/v1/chat/completions", handler.RequestUris[0]!.ToString());
    }

    [Fact]
    public async Task BearerHeaderSentWhenApiKeyProvided()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("ok"));
        var client = new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5), "secret-key");

        await client.CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []);

        Assert.Equal("Bearer secret-key", handler.AuthorizationHeaders[0]);
    }

    [Fact]
    public async Task NoAuthorizationHeaderWithoutApiKey()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("ok"));
        await CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []);

        Assert.Null(handler.AuthorizationHeaders[0]);
    }

    [Fact]
    public async Task EmptyToolListOmitsToolsAndToolChoiceFromTheRequest()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("ok"));
        await CreateClient(handler).CompleteAsync([new ChatMessage { Role = "user", Content = "go" }], []);

        using var request = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
        Assert.False(request.RootElement.TryGetProperty("tool_choice", out _));
    }

    private static ModelClient CreateClient(StubHttpMessageHandler handler) => new(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5));
}
