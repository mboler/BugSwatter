using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Informant;

/// <summary>One candidate finding reported by the primary model before second-opinion validation</summary>
public sealed record CandidateFinding(string? File, int? Line, string Severity, string Summary);

/// <summary>The structured candidate findings parsed from one primary-model answer</summary>
public sealed record ParsedPrimaryReview(IReadOnlyList<CandidateFinding> Findings, Severity MaxSeverity);

/// <summary>Run-level primary severity used to select one second-opinion model</summary>
public sealed record PrimaryReviewClassification(Severity MaxSeverity, bool SeverityDetermined)
{
    /// <summary>Configuration key used by severity routing</summary>
    public string RouteKey => SeverityDetermined ? MaxSeverity.ToString().ToLowerInvariant() : "undetermined";

    /// <summary>Human-readable severity, including the undetermined state</summary>
    public string DisplaySeverity => SeverityDetermined ? MaxSeverity.ToString() : "Undetermined";

    /// <summary>Classifies a complete primary run, failing safe when any attempted review is incomplete or lacks parseable structured severity</summary>
    public static PrimaryReviewClassification FromResults(IEnumerable<FileReviewResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Severity maxSeverity = Severity.None;
        bool severityDetermined = true;

        foreach (FileReviewResult result in results)
        {
            if (result.CandidateSeverity > maxSeverity)
            {
                maxSeverity = result.CandidateSeverity;
            }

            if (result.Status is FileReviewStatus.Failed or FileReviewStatus.Partial
                || result.Status == FileReviewStatus.Reviewed && !result.CandidateSeverityDetermined)
            {
                severityDetermined = false;
            }
        }

        return new PrimaryReviewClassification(maxSeverity, severityDetermined);
    }
}

/// <summary>Extracts the structured candidate findings that the primary model appends after its prose review</summary>
public static partial class PrimaryReviewParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Parses the last valid fenced JSON block and removes it from the human-readable prose</summary>
    /// <param name="modelText">Complete primary-model response</param>
    /// <param name="parsed">Structured candidate findings when parsing succeeds</param>
    /// <param name="prose">Human-readable response without the parsed JSON block, or the original response when parsing fails</param>
    /// <returns>True when the expected structured findings parsed with recognized severity labels</returns>
    public static bool TryParse(string modelText, out ParsedPrimaryReview? parsed, out string prose)
    {
        ArgumentNullException.ThrowIfNull(modelText);
        parsed = null;
        prose = modelText;

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

    private static bool TryDeserialize(string body, out ParsedPrimaryReview? parsed)
    {
        parsed = null;
        try
        {
            PrimaryReviewDto? dto = JsonSerializer.Deserialize<PrimaryReviewDto>(body, JsonOptions);
            if (dto?.Findings is null)
            {
                return false;
            }

            var findings = new List<CandidateFinding>(dto.Findings.Count);
            Severity maxSeverity = Severity.None;
            foreach (CandidateFindingDto item in dto.Findings)
            {
                if (!TryParseCandidateSeverity(item.Severity, out Severity severity))
                {
                    return false;
                }

                if (severity > maxSeverity)
                {
                    maxSeverity = severity;
                }

                findings.Add(new CandidateFinding(item.File, item.Line, item.Severity!.Trim().ToLowerInvariant(), item.Summary ?? ""));
            }

            parsed = new ParsedPrimaryReview(findings, maxSeverity);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseCandidateSeverity(string? label, out Severity severity)
    {
        severity = label?.Trim().ToLowerInvariant() switch
        {
            "critical" => Severity.Critical,
            "high" => Severity.High,
            "medium" => Severity.Medium,
            "low" => Severity.Low,
            _ => Severity.None
        };

        return severity != Severity.None;
    }

    [GeneratedRegex(@"```(?:json)?\s*(?<body>\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FencedJsonRegex();

    private sealed class PrimaryReviewDto
    {
        [JsonPropertyName("findings")]
        public List<CandidateFindingDto>? Findings { get; init; }
    }

    private sealed class CandidateFindingDto
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
}
