using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Informant;

/// <summary>Confirmed-finding severity, ordered so a numeric comparison drives the email gate; None ranks below every real severity</summary>
public enum Severity
{
    /// <summary>No severity, or an unrecognized label on a discarded or malformed entry</summary>
    None = 0,

    /// <summary>Hygiene or hardening</summary>
    Low = 1,

    /// <summary>A defect that needs unusual conditions to bite</summary>
    Medium = 2,

    /// <summary>A defect that bites in normal operation</summary>
    High = 3,

    /// <summary>A crash or data-loss path in normal operation</summary>
    Critical = 4
}

/// <summary>One confirmed finding from the second-opinion model</summary>
public sealed record ConfirmedFinding(string? File, int? Line, string Severity, string Summary);

/// <summary>One finding the second-opinion model discarded as not real</summary>
public sealed record DiscardedFinding(string Summary, string Reason);

/// <summary>The structured verdict parsed from one file's second-opinion answer</summary>
public sealed record ParsedValidation(IReadOnlyList<ConfirmedFinding> Confirmed, IReadOnlyList<DiscardedFinding> Discarded, string? Verdict);

/// <summary>Extracts the machine-readable JSON block the second-opinion model is asked to append, and ranks severities. Parsing is strictly best-effort: a weak model that emits no JSON or malformed JSON must never disturb the prose report, so failure returns false and the caller carries on</summary>
public static partial class SecondOpinionParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Attempts to pull a structured verdict from the model's answer</summary>
    /// <param name="modelText">The full text the model returned, prose followed by an optional fenced json block</param>
    /// <param name="parsed">The structured verdict when parsing succeeds, otherwise null</param>
    /// <param name="prose">The answer with the parsed json block removed on success, or the original text unchanged on failure so nothing is lost</param>
    /// <returns>True when a json block parsed into the expected shape</returns>
    public static bool TryParse(string modelText, out ParsedValidation? parsed, out string prose)
    {
        ArgumentNullException.ThrowIfNull(modelText);
        parsed = null;
        prose = modelText;

        // Try each fenced block from last to first: the model is told to put the json after the prose, so the last one wins
        MatchCollection matches = FencedJsonRegex().Matches(modelText);
        for (int index = matches.Count - 1; index >= 0; index--)
        {
            if (TryDeserialize(matches[index].Groups["body"].Value, out parsed))
            {
                prose = modelText.Remove(matches[index].Index, matches[index].Length).TrimEnd();
                return true;
            }
        }

        return false;
    }

    /// <summary>Highest severity among the confirmed findings, or None when there are none</summary>
    public static Severity MaxSeverity(IEnumerable<ConfirmedFinding> confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        Severity max = Severity.None;
        foreach (ConfirmedFinding finding in confirmed)
        {
            Severity current = ParseSeverity(finding.Severity);
            if (current > max)
            {
                max = current;
            }
        }

        return max;
    }

    /// <summary>Maps a severity label to its rank; a confirmed finding with an unrecognized label is treated as Medium so an odd label never silences the gate</summary>
    public static Severity ParseSeverity(string? label)
    {
        return label?.Trim().ToLowerInvariant() switch
        {
            "critical" => Severity.Critical,
            "high" => Severity.High,
            "medium" => Severity.Medium,
            "low" => Severity.Low,
            null or "" => Severity.None,
            _ => Severity.Medium
        };
    }

    private static bool TryDeserialize(string body, out ParsedValidation? parsed)
    {
        parsed = null;
        try
        {
            ValidationDto? dto = JsonSerializer.Deserialize<ValidationDto>(body, JsonOptions);
            if (dto is null)
            {
                return false;
            }

            IReadOnlyList<ConfirmedFinding> confirmed = [.. (dto.Confirmed ?? []).Select(item => new ConfirmedFinding(item.File, item.Line, item.Severity ?? "", item.Summary ?? ""))];
            IReadOnlyList<DiscardedFinding> discarded = [.. (dto.Discarded ?? []).Select(item => new DiscardedFinding(item.Summary ?? "", item.Reason ?? ""))];
            parsed = new ParsedValidation(confirmed, discarded, dto.Verdict);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"```(?:json)?\s*(?<body>\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex FencedJsonRegex();

    private sealed class ValidationDto
    {
        [JsonPropertyName("confirmed")]
        public List<ConfirmedDto>? Confirmed { get; init; }

        [JsonPropertyName("discarded")]
        public List<DiscardedDto>? Discarded { get; init; }

        [JsonPropertyName("verdict")]
        public string? Verdict { get; init; }
    }

    private sealed class ConfirmedDto
    {
        [JsonPropertyName("file")]
        public string? File { get; init; }

        [JsonPropertyName("line")]
        public int? Line { get; init; }

        [JsonPropertyName("severity")]
        public string? Severity { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }
    }

    private sealed class DiscardedDto
    {
        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }
    }
}
