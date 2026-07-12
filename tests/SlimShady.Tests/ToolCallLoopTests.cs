using System.Net;
using System.Text.Json;

namespace SlimShady.Tests;

public sealed class ToolCallLoopTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task ExecutesToolCallFeedsResultBackAndReturnsFinal()
    {
        File.WriteAllLines(Path.Combine(_root.Path, "code.cs"), ["line one", "line two", "line three"]);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("code.cs", 1, 2))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("review complete"));

        LoopResult result = await CreateLoop(handler).RunAsync("system prompt", "user prompt");

        Assert.Equal("review complete", result.FinalContent);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        JsonElement toolMessage = messages[messages.GetArrayLength() - 1];
        Assert.Equal("tool", toolMessage.GetProperty("role").GetString());
        Assert.Equal("call_1", toolMessage.GetProperty("tool_call_id").GetString());
        Assert.Contains("| line one", toolMessage.GetProperty("content").GetString());
        JsonElement assistantEcho = messages[messages.GetArrayLength() - 2];
        Assert.Equal("assistant", assistantEcho.GetProperty("role").GetString());
        Assert.Equal("call_1", assistantEcho.GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnswersMultipleToolCallsFromOneReplyInOrder()
    {
        File.WriteAllLines(Path.Combine(_root.Path, "a.txt"), ["aaa"]);
        File.WriteAllLines(Path.Combine(_root.Path, "b.txt"), ["bbb"]);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(
            ("call_1", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("a.txt", 1, 1)),
            ("call_2", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("b.txt", 1, 1))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("done"));

        LoopResult result = await CreateLoop(handler).RunAsync("s", "u");

        Assert.Equal(2, result.ToolCallCount);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        int count = messages.GetArrayLength();
        Assert.Equal("call_1", messages[count - 2].GetProperty("tool_call_id").GetString());
        Assert.Contains("aaa", messages[count - 2].GetProperty("content").GetString());
        Assert.Equal("call_2", messages[count - 1].GetProperty("tool_call_id").GetString());
        Assert.Contains("bbb", messages[count - 1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task UnknownToolIsAnsweredWithErrorAndLoopContinues()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", "delete_everything", "{}")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("understood"));

        LoopResult result = await CreateLoop(handler).RunAsync("s", "u");

        Assert.Equal("understood", result.FinalContent);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        Assert.Contains("unknown tool", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task MalformedArgumentsAreAnsweredWithErrorAndLoopContinues()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", ReadFileLinesTool.ToolName, "{ this is broken")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("recovered"));

        LoopResult result = await CreateLoop(handler).RunAsync("s", "u");

        Assert.Equal("recovered", result.FinalContent);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        Assert.Contains("invalid arguments", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task EmptyFinalAnswerThrows()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse(""));
        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => CreateLoop(handler).RunAsync("s", "u"));
        Assert.Contains("empty final answer", ex.Message);
    }

    [Fact]
    public async Task EndlessToolCallingHitsRoundLimit()
    {
        File.WriteAllLines(Path.Combine(_root.Path, "a.txt"), ["aaa"]);
        var handler = new StubHttpMessageHandler();
        for (int i = 0; i < 24; i++)
        {
            handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(($"call_{i}", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("a.txt", 1, 1))));
        }
        ModelCallException ex = await Assert.ThrowsAsync<ModelCallException>(() => CreateLoop(handler).RunAsync("s", "u"));
        Assert.Contains("rounds", ex.Message);
    }

    [Fact]
    public async Task ExhaustedBudgetRefusesFurtherReads()
    {
        File.WriteAllLines(Path.Combine(_root.Path, "a.txt"), ["aaa"]);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("a.txt", 1, 1))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("finished with what I had"));

        // A ten character budget is exceeded by the prompts alone, so the read must be refused
        var loop = new ToolCallLoop(new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5)), new ReadFileLinesTool(_root.Path), 10);
        LoopResult result = await loop.RunAsync("a fairly long system prompt", "a fairly long user prompt");

        Assert.Equal("finished with what I had", result.FinalContent);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        Assert.Contains("context budget exhausted", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    private ToolCallLoop CreateLoop(StubHttpMessageHandler handler) => new(new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5)), new ReadFileLinesTool(_root.Path), 100000);
}
