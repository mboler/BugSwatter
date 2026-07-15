using System.Net;
using System.Text.Json;

namespace BugSwatter.AI.Tests;

public sealed class ToolCallLoopTests
{
    [Fact]
    public async Task ExecutesToolCallFeedsResultBackAndReturnsFinal()
    {
        var tool = new ScriptedTool("read_source", arguments => $"read result: {arguments}");
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", tool.Name, """{"path":"code.cs"}""")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("review complete"));

        LoopResult result = await CreateLoop(handler, tool).RunAsync("system prompt", "user prompt");

        Assert.Equal("review complete", result.FinalContent);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        JsonElement toolMessage = messages[messages.GetArrayLength() - 1];
        Assert.Equal("tool", toolMessage.GetProperty("role").GetString());
        Assert.Equal("call_1", toolMessage.GetProperty("tool_call_id").GetString());
        Assert.Contains("code.cs", toolMessage.GetProperty("content").GetString());
        JsonElement assistantEcho = messages[messages.GetArrayLength() - 2];
        Assert.Equal("assistant", assistantEcho.GetProperty("role").GetString());
        Assert.Equal("call_1", assistantEcho.GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task DistinctToolsAreDispatchedInModelOrder()
    {
        var firstTool = new ScriptedTool("first_tool", _ => "first result");
        var secondTool = new ScriptedTool("second_tool", _ => "second result");
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", firstTool.Name, "{}"), ("call_2", secondTool.Name, "{}")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("done"));
        var loop = new ToolCallLoop(CreateClient(handler), [firstTool, secondTool], 100000);

        LoopResult result = await loop.RunAsync("s", "u");

        Assert.Equal(2, result.ToolCallCount);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        int count = messages.GetArrayLength();
        Assert.Equal("call_1", messages[count - 2].GetProperty("tool_call_id").GetString());
        Assert.Equal("first result", messages[count - 2].GetProperty("content").GetString());
        Assert.Equal("call_2", messages[count - 1].GetProperty("tool_call_id").GetString());
        Assert.Equal("second result", messages[count - 1].GetProperty("content").GetString());
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
    public async Task ToolControlsMalformedArgumentResponseAndLoopContinues()
    {
        var tool = new ScriptedTool("strict_tool", arguments =>
        {
            try
            {
                JsonDocument.Parse(arguments).Dispose();
                return "ok";
            }
            catch (JsonException)
            {
                return "invalid arguments";
            }
        });
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", tool.Name, "{ this is broken")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("recovered"));

        LoopResult result = await CreateLoop(handler, tool).RunAsync("s", "u");

        Assert.Equal("recovered", result.FinalContent);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        Assert.Contains("invalid arguments", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ToolExceptionIsReturnedToTheModelAndTheLoopContinues()
    {
        var events = new List<ModelToolCallAuditEvent>();
        var tool = new ScriptedTool("unreliable_tool", _ => throw new InvalidOperationException("private tool detail"));
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", tool.Name, "{}")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("recovered"));
        var loop = new ToolCallLoop(CreateClient(handler), tool, 100000, auditObserver: events.Add);

        LoopResult result = await loop.RunAsync("s", "u");

        Assert.Equal("recovered", result.FinalContent);
        Assert.Equal(ModelToolCallOutcome.ExecutionFailed, Assert.Single(events).Outcome);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        string toolResult = second.RootElement.GetProperty("messages")[second.RootElement.GetProperty("messages").GetArrayLength() - 1].GetProperty("content").GetString()!;
        Assert.Contains("tool execution failed", toolResult);
        Assert.DoesNotContain("private tool detail", toolResult);
    }

    [Fact]
    public async Task ToolCancellationStillPropagates()
    {
        var tool = new ScriptedTool("cancelled_tool", _ => throw new OperationCanceledException());
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", tool.Name, "{}")));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateLoop(handler, tool).RunAsync("s", "u"));
    }

    [Fact]
    public async Task EmptyFinalAnswerThrows()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse(""));
        ModelCallException exception = await Assert.ThrowsAsync<ModelCallException>(() => CreateLoop(handler).RunAsync("s", "u"));
        Assert.Contains("empty final answer", exception.Message);
    }

    [Fact]
    public async Task EndlessToolCallingHitsRoundLimit()
    {
        var tool = new ScriptedTool("repeat", _ => "again");
        var handler = new StubHttpMessageHandler();
        for (int index = 0; index < 24; index++)
        {
            handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(($"call_{index}", tool.Name, "{}")));
        }

        ModelCallException exception = await Assert.ThrowsAsync<ModelCallException>(() => CreateLoop(handler, tool).RunAsync("s", "u"));
        Assert.Contains("rounds", exception.Message);
    }

    [Fact]
    public async Task ExhaustedBudgetRefusesFurtherCalls()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", "test_tool", "{}")));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("finished with what I had"));
        ToolCallLoop loop = CreateLoop(handler, maxConversationCharacters: 10);

        LoopResult result = await loop.RunAsync("a fairly long system prompt", "a fairly long user prompt");

        Assert.Equal("finished with what I had", result.FinalContent);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        JsonElement messages = second.RootElement.GetProperty("messages");
        Assert.Contains("context budget exhausted", messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());
    }

    /// <summary>Verifies audit events contain bounded metadata and report context refusal without retaining tool bodies</summary>
    [Fact]
    public async Task AuditObserverRecordsArgumentLengthsAndContextRejectionWithoutBodies()
    {
        var events = new List<ModelToolCallAuditEvent>();
        var tool = new ScriptedTool("test_tool", _ => throw new InvalidOperationException("tool must not execute after budget rejection"));
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", tool.Name, JsonSerializer.Serialize(new { value = new string('x', 200) }))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("finished"));
        var loop = new ToolCallLoop(CreateClient(handler), tool, 50, auditObserver: events.Add);

        await loop.RunAsync("system", "user");

        ModelToolCallAuditEvent auditEvent = Assert.Single(events);
        Assert.Equal(ModelToolCallOutcome.ContextBudgetRejected, auditEvent.Outcome);
        Assert.True(auditEvent.ArgumentsCharacters > 200);
        Assert.True(auditEvent.ResultCharacters > 0);
    }

    [Fact]
    public void DuplicateToolNamesAreRejected()
    {
        var first = new ScriptedTool("duplicate", _ => "first");
        var second = new ScriptedTool("duplicate", _ => "second");
        using var http = new HttpClient();
        var client = new ModelClient(http, "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5));

        Assert.Throws<ArgumentException>(() => new ToolCallLoop(client, [first, second], 1000));
    }

    private static ToolCallLoop CreateLoop(StubHttpMessageHandler handler, ScriptedTool? tool = null, int maxConversationCharacters = 100000)
        => new(CreateClient(handler), tool ?? new ScriptedTool("test_tool", arguments => arguments), maxConversationCharacters);

    private static ModelClient CreateClient(StubHttpMessageHandler handler) => new(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5));

    private sealed class ScriptedTool(string name, Func<string, string> execute) : IModelTool
    {
        public string Name => name;

        public ToolDefinition Definition { get; } = new()
        {
            Function = new FunctionDefinition
            {
                Name = name,
                Description = "A scripted test tool",
                Parameters = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()
            }
        };

        public string Execute(string argumentsJson) => execute(argumentsJson);
    }
}
