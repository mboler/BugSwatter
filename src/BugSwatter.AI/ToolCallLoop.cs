using System.Text.Json;
using Serilog;

namespace BugSwatter.AI;

/// <summary>Outcome of one completed tool-call conversation</summary>
public sealed record LoopResult(string FinalContent, int ToolCallCount);

/// <summary>Drives a tool-call and tool-result exchange: system and user prompt in, final assistant text out. The caller executes every tool and feeds the result back; the model never touches anything directly</summary>
public sealed class ToolCallLoop
{
    private const int MaxRounds = 24;

    private readonly ModelClient _client;
    private readonly IReadOnlyList<ToolDefinition> _definitions;
    private readonly Dictionary<string, IModelTool> _toolsByName;
    private readonly int _maxConversationCharacters;
    private readonly int _maxToolCalls;

    /// <summary>Creates a loop bound to one client, one tool and a conversation character budget</summary>
    /// <param name="client">The model client that runs each completion round</param>
    /// <param name="tool">The tool exposed to the model</param>
    /// <param name="maxConversationCharacters">Character budget for the running conversation; once it is exceeded further tool reads are refused so the model finishes with what it has</param>
    /// <param name="maxToolCalls">Hard cap on how many tool calls are honored; further calls are refused so the model finishes. Defaults to unlimited (bounded only by the round limit and the character budget)</param>
    public ToolCallLoop(ModelClient client, IModelTool tool, int maxConversationCharacters, int maxToolCalls = int.MaxValue) : this(client, [tool], maxConversationCharacters, maxToolCalls)
    { }

    /// <summary>Creates a loop bound to one client, one or more tools and a conversation character budget</summary>
    /// <param name="client">The model client that runs each completion round</param>
    /// <param name="tools">Distinctly named tools exposed to the model</param>
    /// <param name="maxConversationCharacters">Character budget for the running conversation; once it is exceeded further tool calls are refused so the model finishes with what it has</param>
    /// <param name="maxToolCalls">Hard cap on how many tool calls are honored; further calls are refused so the model finishes. Defaults to unlimited (bounded only by the round limit and the character budget)</param>
    public ToolCallLoop(ModelClient client, IReadOnlyList<IModelTool> tools, int maxConversationCharacters, int maxToolCalls = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConversationCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxToolCalls);
        if (tools.Count == 0)
        {
            throw new ArgumentException("At least one model tool is required", nameof(tools));
        }

        _client = client;
        _toolsByName = new Dictionary<string, IModelTool>(StringComparer.Ordinal);
        foreach (IModelTool tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            string name = tool.Definition.Function.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Every model tool requires a non-empty function name", nameof(tools));
            }

            if (!_toolsByName.TryAdd(name, tool))
            {
                throw new ArgumentException($"Model tool name '{name}' is duplicated", nameof(tools));
            }
        }

        _definitions = [.. tools.Select(tool => tool.Definition)];
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
            var reply = await _client.CompleteAsync(messages, _definitions, cancellationToken);
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
                    ? """{"error": "tool-call budget reached; no more tool calls are allowed, finish with the information already available"}"""
                    : ExecuteCall(call, messages);
                messages.Add(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = result });
            }
        }

        throw new ModelCallException($"Model did not produce a final answer within {MaxRounds} tool-call rounds");
    }

    private string ExecuteCall(ToolCall call, List<ChatMessage> messages)
    {
        string? name = call.Function?.Name;
        if (name is null || !_toolsByName.TryGetValue(name, out IModelTool? tool))
        {
            Log.Warning("Model called unknown tool {Tool}", name ?? "(null)");
            return JsonSerializer.Serialize(new { error = $"unknown tool '{name}'; available tools: {string.Join(", ", _toolsByName.Keys)}" });
        }

        // Once the conversation exceeds the budget, stop feeding more tool content; the model must finish with what it has
        if (ConversationCharacters(messages) > _maxConversationCharacters)
        {
            Log.Warning("Conversation exceeded the {Budget} character budget; further tool calls are refused", _maxConversationCharacters);
            return """{"error": "context budget exhausted; no more tool content can be provided, finish with the information already available"}""";
        }

        return tool.Execute(call.Function?.Arguments ?? "");
    }

    private static int ConversationCharacters(List<ChatMessage> messages) => messages.Sum(message => message.Content?.Length ?? 0);
}
