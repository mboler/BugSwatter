namespace BugSwatter.AI;

/// <summary>Lifecycle point reported for a non-streaming model request</summary>
public enum ModelCallState
{
    /// <summary>The HTTP request is about to begin</summary>
    Started,

    /// <summary>The provider returned a valid assistant message</summary>
    Completed,

    /// <summary>The request, response, or response parsing failed</summary>
    Failed
}

/// <summary>Token counts reported by an OpenAI-compatible provider for one completed response; any field may be absent</summary>
public sealed record ModelTokenUsage(long? PromptTokens, long? CompletionTokens, long? TotalTokens);

/// <summary>Best-effort telemetry for one model request; it never changes request behavior and completed usage is provider-reported rather than estimated</summary>
public sealed record ModelCallProgress(ModelCallState State, string ModelName, DateTimeOffset StartedUtc, TimeSpan Duration, ModelTokenUsage? Usage, int RequestCharacters = 0);
