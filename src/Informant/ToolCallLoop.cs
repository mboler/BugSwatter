using Serilog;

namespace Informant;

/// <summary>Outcome of one completed tool-call conversation</summary>
public sealed record LoopResult(string FinalContent, int ToolCallCount);

/// <summary>Drives the tool-call and tool-result exchange for one unit of work: system and user prompt in, final assistant text out. Informant executes every tool call itself and feeds the result back; the model never touches anything directly</summary>
public sealed class ToolCallLoop
{
    private const int MaxRounds = 24;

    private readonly ModelClient _client;
    private readonly ReadFileLinesTool _tool;
    private readonly int _maxConversationCharacters;
    private readonly int _maxToolCalls;

    /// <summary>Creates a loop bound to one client, one tool and a conversation character budget</summary>
    /// <param name="client">The model client that runs each completion round</param>
    /// <param name="tool">The single read-file tool exposed to the model</param>
    /// <param name="maxConversationCharacters">Character budget for the running conversation; once it is exceeded further tool reads are refused so the model finishes with what it has</param>
    /// <param name="maxToolCalls">Hard cap on how many tool calls are honored; further calls are refused so the model finishes. Defaults to unlimited (bounded only by the round limit and the character budget)</param>
    public ToolCallLoop(ModelClient client, ReadFileLinesTool tool, int maxConversationCharacters, int maxToolCalls = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tool);

        _client = client;
        _tool = tool;
        _maxConversationCharacters = maxConversationCharacters;
        _maxToolCalls = maxToolCalls;
    }

    /// <summary>Runs the exchange until the model returns a final text answer; throws <see cref="ModelCallException"/> on empty answers or when the round limit is hit</summary>
    public async Task<LoopResult> RunAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> messages = [new() { Role = "system", Content = systemPrompt }, new() { Role = "user", Content = userPrompt }];
        int toolCallCount = 0;

        for (int round = 1; round <= MaxRounds; round++)
        {
            var reply = await _client.CompleteAsync(messages, [ReadFileLinesTool.Definition], cancellationToken);
            messages.Add(reply);

            if (reply.ToolCalls is not { Count: > 0 })
            {
                if (string.IsNullOrWhiteSpace(reply.Content))
                {
                    throw new ModelCallException("Model returned an empty final answer");
                }

                return new LoopResult(reply.Content, toolCallCount);
            }

            foreach (ToolCall call in reply.ToolCalls)
            {
                toolCallCount++;
                string result = toolCallCount > _maxToolCalls
                    ? """{"error": "read budget reached; no more file reads are allowed, finish your review with the information you already have"}"""
                    : ExecuteCall(call, messages);
                messages.Add(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = result });
            }
        }

        throw new ModelCallException($"Model did not produce a final answer within {MaxRounds} tool-call rounds");
    }

    private string ExecuteCall(ToolCall call, List<ChatMessage> messages)
    {
        string? name = call.Function?.Name;
        if (!string.Equals(name, ReadFileLinesTool.ToolName, StringComparison.Ordinal))
        {
            Log.Warning("Model called unknown tool {Tool}", name ?? "(null)");
            return $$"""{"error": "unknown tool '{{name}}'; the only available tool is {{ReadFileLinesTool.ToolName}}"}""";
        }

        // Once the conversation exceeds the budget, stop feeding more file content; the model must finish with what it has
        if (ConversationCharacters(messages) > _maxConversationCharacters)
        {
            Log.Warning("Conversation exceeded the {Budget} character budget; further reads are refused", _maxConversationCharacters);
            return """{"error": "context budget exhausted; no more file content can be provided, finish the review with the information you already have"}""";
        }

        return _tool.ExecuteRaw(call.Function?.Arguments ?? "");
    }

    private static int ConversationCharacters(List<ChatMessage> messages) => messages.Sum(message => message.Content?.Length ?? 0);
}
