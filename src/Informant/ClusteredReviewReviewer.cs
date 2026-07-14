using System.Text;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Attributed findings and severity state for one supplied source part</summary>
public sealed record ReviewUnitPartResult(ReviewUnitPart Part, string Findings, Severity CandidateSeverity, bool CandidateSeverityDetermined);

/// <summary>Outcome of one clustered model conversation</summary>
public sealed record ReviewUnitResult(ReviewExecutionUnit Unit, IReadOnlyList<ReviewUnitPartResult> PartResults, FileReviewFailureKind FailureKind, string? FailureReason,
    string? ReviewModelName = null, string? ReviewModelProfile = null)
{
    /// <summary>Whether the complete unit produced attributable results</summary>
    public bool Succeeded => FailureKind == FileReviewFailureKind.None;
}

/// <summary>Runs one bounded clustered review conversation and retries model-layer failures without losing completed earlier units</summary>
public sealed class ClusteredReviewReviewer
{
    private readonly ToolCallLoop _loop;
    private readonly string _systemPrompt;
    private readonly int _retryCount;
    private readonly Action<ReviewContextSelectionEvent>? _contextObserver;

    /// <summary>Creates a clustered reviewer for one already-running model</summary>
    public ClusteredReviewReviewer(ToolCallLoop loop, string systemPrompt, int retryCount, Action<ReviewContextSelectionEvent>? contextObserver = null)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentOutOfRangeException.ThrowIfNegative(retryCount);

        _loop = loop;
        _systemPrompt = systemPrompt;
        _retryCount = retryCount;
        _contextObserver = contextObserver;
    }

    /// <summary>Reviews every supplied part in one conversation or returns a model failure after configured retries</summary>
    public async Task<ReviewUnitResult> ReviewAsync(ReviewExecutionUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        string? lastReason = null;
        for (int attempt = 0; attempt <= _retryCount; attempt++)
        {
            try
            {
                ObserveSelections(unit);
                LoopResult response = await _loop.RunAsync(_systemPrompt, unit.UserPrompt, cancellationToken);
                if (ClusteredReviewResponseParser.TryParse(response.FinalContent, unit, out IReadOnlyList<ReviewUnitPartResult> results))
                {
                    Log.Information("Reviewed unit {Unit} with {Parts} parts and {ToolCalls} tool calls", unit.Id, unit.Parts.Count, response.ToolCallCount);
                    return new ReviewUnitResult(unit, results, FileReviewFailureKind.None, null);
                }

                lastReason = "model response could not be attributed to every source part";
                Log.Warning("Review unit {Unit} attempt {Attempt}/{Attempts} returned an unusable clustered response", unit.Id, attempt + 1, _retryCount + 1);
            }
            catch (ModelCallException ex)
            {
                lastReason = ex.Message;
                Log.Warning("Review unit {Unit} attempt {Attempt}/{Attempts} failed: {Reason}", unit.Id, attempt + 1, _retryCount + 1, ex.Message);
            }
        }

        return new ReviewUnitResult(unit, [], FileReviewFailureKind.Model, lastReason ?? "clustered review failed without a reason");
    }

    private void ObserveSelections(ReviewExecutionUnit unit)
    {
        if (_contextObserver is null)
        {
            return;
        }

        foreach (ReviewUnitPart part in unit.Parts)
        {
            try
            {
                _contextObserver(new ReviewContextSelectionEvent(part.File.Path, part.StartLine, part.EndLine, part.EndLine - part.StartLine + 1, part.ContentCharacters));
            }
            catch (Exception ex)
            {
                // Catch-all: optional audit telemetry must never alter clustered source selection or model results.
                Log.Warning("Clustered review context observer failed: {Reason}", ex.Message);
            }
        }
    }
}

/// <summary>Attributes marked clustered prose and the existing structured severity block to exact supplied source parts</summary>
public static class ClusteredReviewResponseParser
{
    /// <summary>Parses a clustered response, accepting deterministic structured-summary fallback when a model omits prose markers</summary>
    public static bool TryParse(string modelText, ReviewExecutionUnit unit, out IReadOnlyList<ReviewUnitPartResult> results)
    {
        ArgumentNullException.ThrowIfNull(modelText);
        ArgumentNullException.ThrowIfNull(unit);

        bool structuredParsed = PrimaryReviewParser.TryParse(modelText, out ParsedPrimaryReview? parsed, out string prose);
        Dictionary<string, string>? sections = ParseMarkedSections(prose, unit.Parts);
        if (sections is null && !structuredParsed)
        {
            results = [];
            return false;
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var knownPaths = new HashSet<string>(unit.Parts.Select(part => part.File.Path), comparer);
        bool structuredValid = structuredParsed && parsed!.Findings.All(finding => finding.File is not null && TryCanonicalKnownPath(finding.File, knownPaths, out _));
        var partResults = new List<ReviewUnitPartResult>(unit.Parts.Count);
        foreach (ReviewUnitPart part in unit.Parts)
        {
            CandidateFinding[] fileFindings = structuredParsed
                ? [.. parsed!.Findings.Where(finding => finding.File is not null && PathsEqual(finding.File, part.File.Path, comparer))]
                : [];
            Severity severity = fileFindings.Select(finding => ParseSeverity(finding.Severity)).DefaultIfEmpty(Severity.None).Max();
            string findings = sections is not null ? sections[part.Id] : BuildStructuredSummary(part, fileFindings);
            partResults.Add(new ReviewUnitPartResult(part, findings, severity, structuredValid));
        }

        results = partResults;
        return true;
    }

    private static Dictionary<string, string>? ParseMarkedSections(string prose, IReadOnlyList<ReviewUnitPart> parts)
    {
        var positions = new List<(ReviewUnitPart Part, int Position, string Marker)>(parts.Count);
        foreach (ReviewUnitPart part in parts)
        {
            string marker = Marker(part.Id);
            int position = prose.IndexOf(marker, StringComparison.Ordinal);
            if (position < 0 || prose.IndexOf(marker, position + marker.Length, StringComparison.Ordinal) >= 0)
            {
                return null;
            }

            positions.Add((part, position, marker));
        }

        positions.Sort((left, right) => left.Position.CompareTo(right.Position));
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < positions.Count; index++)
        {
            (ReviewUnitPart part, int position, string marker) = positions[index];
            int start = position + marker.Length;
            int end = index + 1 < positions.Count ? positions[index + 1].Position : prose.Length;
            string section = prose[start..end].Trim();
            sections.Add(part.Id, section.Length == 0 ? "No candidate findings were reported for this source part." : section);
        }

        return sections;
    }

    private static string BuildStructuredSummary(ReviewUnitPart part, IReadOnlyList<CandidateFinding> findings)
    {
        if (part.PartNumber > 1)
        {
            return "The model omitted per-part prose markers; file-level structured findings are recorded with the first source part.";
        }

        if (findings.Count == 0)
        {
            return "No candidate findings were reported for this source part.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"The model returned structured findings for `{part.File.Path}` but did not preserve the requested per-part prose markers:");
        foreach (CandidateFinding finding in findings)
        {
            string location = finding.Line is null ? "line not supplied" : $"line {finding.Line}";
            builder.AppendLine($"- {finding.Severity}: {finding.Summary} ({location})");
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryCanonicalKnownPath(string path, IReadOnlySet<string> knownPaths, out string canonical)
    {
        canonical = "";
        if (!RepositoryRelativePath.TryNormalize(path, out string normalized))
        {
            return false;
        }

        string? matched = knownPaths.FirstOrDefault(known => PathsEqual(known, normalized, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        if (matched is null)
        {
            return false;
        }

        canonical = matched;
        return true;
    }

    private static bool PathsEqual(string left, string right, StringComparer comparer) => RepositoryRelativePath.TryNormalize(left, out string normalized) && comparer.Equals(normalized, right);

    private static Severity ParseSeverity(string label) => label switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "medium" => Severity.Medium,
        "low" => Severity.Low,
        _ => Severity.None
    };

    private static string Marker(string partId) => $"=== BUGSWATTER RESULT {partId} ===";
}

/// <summary>Combines completed and failed clustered parts into the existing one-result-per-file contract</summary>
public static class ClusteredReviewResultAggregator
{
    /// <summary>Builds one final file result in original detection order for reporting, second opinion, and baseline decisions</summary>
    public static IReadOnlyList<FileReviewResult> Build(IReadOnlyList<ChangedFile> files, ClusteredReviewBuild build, IReadOnlyList<ReviewUnitResult> unitResults)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(unitResults);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, FileReviewResult> immediate = build.ImmediateResults.ToDictionary(result => result.File.Path, comparer);
        var successes = new Dictionary<string, (ReviewUnitPartResult Part, ReviewUnitResult Unit)>(StringComparer.Ordinal);
        var failures = build.PartFailures.ToDictionary(failure => failure.Part.Id, failure => failure.Reason, StringComparer.Ordinal);
        foreach (ReviewUnitResult unitResult in unitResults)
        {
            if (unitResult.Succeeded)
            {
                foreach (ReviewUnitPartResult partResult in unitResult.PartResults)
                {
                    successes.Add(partResult.Part.Id, (partResult, unitResult));
                }
            }
            else
            {
                foreach (ReviewUnitPart part in unitResult.Unit.Parts)
                {
                    failures[part.Id] = unitResult.FailureReason ?? "clustered model review failed";
                }
            }
        }

        var results = new List<FileReviewResult>(files.Count);
        foreach (ChangedFile file in files)
        {
            if (immediate.TryGetValue(file.Path, out FileReviewResult? immediateResult))
            {
                results.Add(immediateResult);
                continue;
            }

            ReviewUnitPart[] parts = [.. build.Parts.Where(part => comparer.Equals(part.File.Path, file.Path)).OrderBy(part => part.PartNumber)];
            if (parts.Length == 0)
            {
                results.Add(new FileReviewResult(file, FileReviewStatus.Failed, null, 0, 0, "validated review plan produced no source part", FailureKind: FileReviewFailureKind.Repository));
                continue;
            }

            List<(ReviewUnitPartResult Part, ReviewUnitResult Unit)> completed = [.. parts.Where(part => successes.ContainsKey(part.Id)).Select(part => successes[part.Id])];
            FileReviewStatus status = completed.Count == parts.Length ? FileReviewStatus.Reviewed : completed.Count == 0 ? FileReviewStatus.Failed : FileReviewStatus.Partial;
            string? findings = BuildFindings(parts, completed);
            Severity severity = completed.Select(result => result.Part.CandidateSeverity).DefaultIfEmpty(Severity.None).Max();
            bool severityDetermined = status == FileReviewStatus.Reviewed && completed.All(result => result.Part.CandidateSeverityDetermined);
            string? reason = status == FileReviewStatus.Reviewed ? null : BuildFailureReason(parts, failures, completed.Count);
            string? modelNames = JoinModels(completed.Select(result => result.Unit.ReviewModelName));
            string? modelProfiles = JoinModels(completed.Select(result => result.Unit.ReviewModelProfile));
            results.Add(new FileReviewResult(file, status, findings, completed.Count, parts.Length, reason, severity, severityDetermined,
                status == FileReviewStatus.Reviewed ? FileReviewFailureKind.None : FileReviewFailureKind.Model, modelNames, modelProfiles));
        }

        return results;
    }

    private static string? BuildFindings(IReadOnlyList<ReviewUnitPart> parts, IReadOnlyList<(ReviewUnitPartResult Part, ReviewUnitResult Unit)> completed)
    {
        if (completed.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach ((ReviewUnitPartResult result, _) in completed.OrderBy(item => item.Part.Part.PartNumber))
        {
            if (parts.Count > 1)
            {
                builder.AppendLine($"### Part {result.Part.PartNumber} of {result.Part.TotalParts} (lines {result.Part.StartLine}-{result.Part.EndLine})");
                builder.AppendLine();
            }

            builder.AppendLine(result.Findings.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string BuildFailureReason(IReadOnlyList<ReviewUnitPart> parts, IReadOnlyDictionary<string, string> failures, int completedCount)
    {
        string details = string.Join("; ", parts.Where(part => failures.ContainsKey(part.Id)).Select(part => $"part {part.PartNumber}: {failures[part.Id]}").Distinct(StringComparer.Ordinal));
        return $"{completedCount} of {parts.Count} clustered source parts completed{(details.Length == 0 ? "" : $"; {details}")}";
    }

    private static string? JoinModels(IEnumerable<string?> values)
    {
        string[] distinct = [.. values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).Distinct(StringComparer.Ordinal)];
        return distinct.Length == 0 ? null : string.Join(", ", distinct);
    }
}
