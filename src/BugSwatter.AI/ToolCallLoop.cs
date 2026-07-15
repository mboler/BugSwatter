using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace BugSwatter.AI;

/// <summary>Outcome of one completed tool-call conversation</summary>
public sealed record LoopResult(string FinalContent, int ToolCallCount);

/// <summary>Disposition of one tool call handled by the generic conversation loop</summary>
public enum ModelToolCallOutcome
{
    /// <summary>The configured tool executed and returned a result</summary>
    Executed,

    /// <summary>The model requested a tool that was not exposed</summary>
    UnknownTool,

    /// <summary>The configured per-conversation call count was exhausted</summary>
    CallBudgetRejected,

    /// <summary>The conversation character budget was already exhausted</summary>
    ContextBudgetRejected,

    /// <summary>The configured tool raised an unexpected exception</summary>
    ExecutionFailed
}

/// <summary>Metadata-only observation of one tool call handled by the conversation loop</summary>
public sealed record ModelToolCallAuditEvent(string? ToolName, ModelToolCallOutcome Outcome, int ArgumentsCharacters, int ResultCharacters, long DurationMilliseconds);

/// <summary>Drives a tool-call and tool-result exchange: system and user prompt in, final assistant text out. The caller executes every tool and feeds the result back; the model never touches anything directly</summary>
public sealed class ToolCallLoop
{
    private const int MaxRounds = 24;

    private readonly ModelClient _client;
    private readonly IReadOnlyList<ToolDefinition> _definitions;
    private readonly Dictionary<string, IModelTool> _toolsByName;
    private readonly int _maxConversationCharacters;
    private readonly int _maxToolCalls;
    private readonly Action<ModelToolCallAuditEvent>? _auditObserver;

    /// <summary>Creates a loop bound to one client, one tool and a conversation character budget</summary>
    /// <param name="client">The model client that runs each completion round</param>
    /// <param name="tool">The tool exposed to the model</param>
    /// <param name="maxConversationCharacters">Character budget for the running conversation; once it is exceeded further tool reads are refused so the model finishes with what it has</param>
    /// <param name="maxToolCalls">Hard cap on how many tool calls are honored; further calls are refused so the model finishes. Defaults to unlimited (bounded only by the round limit and the character budget)</param>
    /// <param name="auditObserver">Optional metadata-only observer for tool dispatch outcomes</param>
    public ToolCallLoop(ModelClient client, IModelTool tool, int maxConversationCharacters, int maxToolCalls = int.MaxValue, Action<ModelToolCallAuditEvent>? auditObserver = null)
        : this(client, [tool], maxConversationCharacters, maxToolCalls, auditObserver)
    { }

    /// <summary>Creates a loop bound to one client, one or more tools and a conversation character budget</summary>
    /// <param name="client">The model client that runs each completion round</param>
    /// <param name="tools">Distinctly named tools exposed to the model</param>
    /// <param name="maxConversationCharacters">Character budget for the running conversation; once it is exceeded further tool calls are refused so the model finishes with what it has</param>
    /// <param name="maxToolCalls">Hard cap on how many tool calls are honored; further calls are refused so the model finishes. Defaults to unlimited (bounded only by the round limit and the character budget)</param>
    /// <param name="auditObserver">Optional metadata-only observer for tool dispatch outcomes</param>
    public ToolCallLoop(ModelClient client, IReadOnlyList<IModelTool> tools, int maxConversationCharacters, int maxToolCalls = int.MaxValue, Action<ModelToolCallAuditEvent>? auditObserver = null)
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
        _auditObserver = auditObserver;
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
                string result;
                if (toolCallCount > _maxToolCalls)
                {
                    result = """{"error": "tool-call budget reached; no more tool calls are allowed, finish with the information already available"}""";
                    Observe(new ModelToolCallAuditEvent(call.Function?.Name, ModelToolCallOutcome.CallBudgetRejected, call.Function?.Arguments?.Length ?? 0, result.Length, 0));
                }
                else
                {
                    result = ExecuteCall(call, messages);
                }

                messages.Add(new ChatMessage { Role = "tool", ToolCallId = call.Id, Content = result });
            }
        }

        throw new ModelCallException($"Model did not produce a final answer within {MaxRounds} tool-call rounds");
    }

    private string ExecuteCall(ToolCall call, List<ChatMessage> messages)
    {
        long started = Stopwatch.GetTimestamp();
        string? name = call.Function?.Name;
        if (name is null || !_toolsByName.TryGetValue(name, out IModelTool? tool))
        {
            Log.Warning("Model called unknown tool {Tool}", name ?? "(null)");
            string result = JsonSerializer.Serialize(new { error = $"unknown tool '{name}'; available tools: {string.Join(", ", _toolsByName.Keys)}" });
            Observe(new ModelToolCallAuditEvent(name, ModelToolCallOutcome.UnknownTool, call.Function?.Arguments?.Length ?? 0, result.Length, ElapsedMilliseconds(started)));
            return result;
        }

        // Once the conversation exceeds the budget, stop feeding more tool content; the model must finish with what it has
        if (ConversationCharacters(messages) > _maxConversationCharacters)
        {
            Log.Warning("Conversation exceeded the {Budget} character budget; further tool calls are refused", _maxConversationCharacters);
            const string result = """{"error": "context budget exhausted; no more tool content can be provided, finish with the information already available"}""";
            Observe(new ModelToolCallAuditEvent(name, ModelToolCallOutcome.ContextBudgetRejected, call.Function?.Arguments?.Length ?? 0, result.Length, ElapsedMilliseconds(started)));
            return result;
        }

        try
        {
            string toolResult = tool.Execute(call.Function?.Arguments ?? "");
            Observe(new ModelToolCallAuditEvent(name, ModelToolCallOutcome.Executed, call.Function?.Arguments?.Length ?? 0, toolResult.Length, ElapsedMilliseconds(started)));
            return toolResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Catch-all: an unexpected tool failure is returned as bounded model-visible data so one read cannot abort an unattended review
            Log.Warning("Model tool {Tool} failed: {Reason}", name, ex.Message);
            const string result = """{"error": "tool execution failed; inspect a different valid path or finish with the information already available"}""";
            Observe(new ModelToolCallAuditEvent(name, ModelToolCallOutcome.ExecutionFailed, call.Function?.Arguments?.Length ?? 0, result.Length, ElapsedMilliseconds(started)));
            return result;
        }
    }

    private void Observe(ModelToolCallAuditEvent auditEvent)
    {
        try
        {
            _auditObserver?.Invoke(auditEvent);
        }
        catch (Exception ex)
        {
            // Catch-all: optional audit telemetry must never alter tool dispatch or model results.
            Log.Warning("Model tool-call audit observer failed: {Reason}", ex.Message);
        }
    }

    private static long ConversationCharacters(List<ChatMessage> messages) => messages.Sum(message => (long)(message.Content?.Length ?? 0)
        + (message.ToolCalls?.Sum(call => (call.Id?.Length ?? 0) + (call.Function?.Name?.Length ?? 0) + (call.Function?.Arguments?.Length ?? 0)) ?? 0));

    private static long ElapsedMilliseconds(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
