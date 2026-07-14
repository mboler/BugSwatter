using System.Text.Json;

namespace BugSwatter.Common;

/// <summary>A complete, non-secret snapshot of an Informant review suitable for a supervising process or line-oriented script</summary>
public sealed record ReviewProgressSnapshot
{
    /// <summary>Protocol version carried on every snapshot</summary>
    public int Version { get; init; } = ReviewProgressMarker.CurrentVersion;

    /// <summary>Current human-readable review phase</summary>
    public required string Phase { get; init; }

    /// <summary>Model currently selected for the phase, when the phase uses a model</summary>
    public string? ModelName { get; init; }

    /// <summary>Logical profile such as primary or a named second-opinion profile</summary>
    public string? ModelProfile { get; init; }

    /// <summary>Repository-relative file currently under review</summary>
    public string? CurrentFile { get; init; }

    /// <summary>One-based position of the current file</summary>
    public int? FileIndex { get; init; }

    /// <summary>Number of files in the current review pass</summary>
    public int? FileCount { get; init; }

    /// <summary>True while an HTTP model request is awaiting a response</summary>
    public bool ModelRequestActive { get; init; }

    /// <summary>When the active model request began, or null between requests</summary>
    public DateTimeOffset? ModelRequestStartedUtc { get; init; }

    /// <summary>Number of model requests started during this Informant run</summary>
    public int ModelRequestCount { get; init; }

    /// <summary>Provider-reported prompt token total across completed responses, or null when not reported</summary>
    public long? PromptTokens { get; init; }

    /// <summary>Provider-reported completion token total across completed responses, or null when not reported</summary>
    public long? CompletionTokens { get; init; }

    /// <summary>Provider-reported total token count across completed responses, or null when not reported</summary>
    public long? TotalTokens { get; init; }
}

/// <summary>Versioned stdout marker used to exchange review progress without giving Informant a Marshal dependency</summary>
public static class ReviewProgressMarker
{
    /// <summary>Current progress protocol version</summary>
    public const int CurrentVersion = 1;

    /// <summary>Prefix placed before one compact JSON snapshot on each progress line</summary>
    public const string Prefix = "INFORMANT-PROGRESS:";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Formats one complete snapshot as a single stdout line</summary>
    public static string Format(ReviewProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return $"{Prefix} {JsonSerializer.Serialize(snapshot, JsonOptions)}";
    }

    /// <summary>Parses and validates a supported progress line, returning false for ordinary output, malformed JSON, invalid values, or an unsupported version</summary>
    public static bool TryParse(string line, out ReviewProgressSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            ReviewProgressSnapshot? parsed = JsonSerializer.Deserialize<ReviewProgressSnapshot>(trimmed[Prefix.Length..].Trim(), JsonOptions);
            if (!IsValid(parsed))
            {
                return false;
            }

            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValid(ReviewProgressSnapshot? snapshot) => snapshot is not null && snapshot.Version == CurrentVersion && !string.IsNullOrWhiteSpace(snapshot.Phase)
        && snapshot.ModelRequestCount >= 0 && HasValidFilePosition(snapshot.FileIndex, snapshot.FileCount) && IsNullableNonNegative(snapshot.PromptTokens)
        && IsNullableNonNegative(snapshot.CompletionTokens) && IsNullableNonNegative(snapshot.TotalTokens) && (!snapshot.ModelRequestActive || snapshot.ModelRequestStartedUtc is not null);

    private static bool HasValidFilePosition(int? fileIndex, int? fileCount) => (fileIndex is null && fileCount is null) || (fileIndex > 0 && fileCount > 0 && fileIndex <= fileCount);

    private static bool IsNullableNonNegative(long? value) => value is null or >= 0;
}
