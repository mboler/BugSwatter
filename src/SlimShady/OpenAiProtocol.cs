using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimShady;

/// <summary>One chat message in the OpenAI-compatible wire format; assistant replies carrying tool calls are echoed back verbatim, and tool results reference the call they answer</summary>
public sealed record ChatMessage
{
    /// <summary>Message role: system, user, assistant or tool</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>Message text; null on assistant messages that only carry tool calls</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>Tool calls requested by the assistant</summary>
    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>On tool messages, the id of the call this result answers</summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }
}

/// <summary>A tool invocation requested by the model</summary>
public sealed record ToolCall
{
    /// <summary>Server-assigned call id, echoed back with the result</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Call type, always "function" in this protocol</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The function being invoked</summary>
    [JsonPropertyName("function")]
    public ToolCallFunction? Function { get; init; }
}

/// <summary>Function name and raw JSON arguments of a tool call</summary>
public sealed record ToolCallFunction
{
    /// <summary>Name of the called function</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Arguments as a raw JSON string, exactly as the model produced them</summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }
}

/// <summary>A tool made available to the model</summary>
public sealed record ToolDefinition
{
    /// <summary>Tool type, always "function"</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    /// <summary>The function declaration</summary>
    [JsonPropertyName("function")]
    public required FunctionDefinition Function { get; init; }
}

/// <summary>Declares a callable function: name, model-facing description and JSON schema of the parameters</summary>
public sealed record FunctionDefinition
{
    /// <summary>Function name the model calls</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Description surfaced to the model</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>JSON schema of the parameters object</summary>
    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; init; }
}

/// <summary>Request body for the chat-completions call</summary>
public sealed record ChatRequest
{
    /// <summary>Model name</summary>
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    /// <summary>Conversation so far</summary>
    [JsonPropertyName("messages")]
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>Tools offered to the model</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    /// <summary>Tool-choice policy; "auto" lets the model decide when to call</summary>
    [JsonPropertyName("tool_choice")]
    public string? ToolChoice { get; init; }
}

/// <summary>Response body of the chat-completions call; unknown fields are ignored</summary>
public sealed record ChatResponse
{
    /// <summary>Completion choices; SlimShady uses the first</summary>
    [JsonPropertyName("choices")]
    public IReadOnlyList<ChatChoice>? Choices { get; init; }
}

/// <summary>One completion choice</summary>
public sealed record ChatChoice
{
    /// <summary>The assistant message produced</summary>
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; init; }

    /// <summary>Why generation stopped, for example "stop" or "tool_calls"</summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}
