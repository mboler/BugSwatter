using System.Text;
using Serilog;

namespace Informant;

/// <summary>
/// Writes deterministic run metadata and model findings, appending completed file or clustered-unit sections immediately so a later interruption does not lose finished work.
/// </summary>
public sealed class ReportWriter
{
    private const string PendingReviewed = "(pending: files reviewed)";
    private const string PendingSkipped = "(pending: files skipped)";
    private const string PendingDuration = "(pending: run duration)";
    private const string PendingCompleted = "(pending: run completed)";

    private readonly string _path;
    private readonly string _modelName;
    private readonly string _modelEndpoint;
    private readonly int _maxContextCharacters;
    private readonly int _maxFileLines;
    private readonly IReadOnlyList<PrimaryModelTarget> _modelTargets;
    private readonly string? _traceFileName;
    private DateTimeOffset _startedAt;

    /// <summary>Creates the report writer for this run; the review model, endpoint and context settings are recorded in the header</summary>
    public ReportWriter(string directory, string runStamp, string modelName, string modelEndpoint, int maxContextCharacters, int maxFileLines, IReadOnlyList<PrimaryModelTarget>? modelTargets = null,
        string? traceFileName = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"Informant-Report-{runStamp}.md");
        _modelName = modelName;
        _modelEndpoint = modelEndpoint;
        _maxContextCharacters = maxContextCharacters;
        _maxFileLines = maxFileLines;
        _modelTargets = modelTargets ?? [new PrimaryModelTarget("primary", modelEndpoint, modelName, false)];
        _traceFileName = traceFileName;
    }

    /// <summary>Full path of the report file</summary>
    public string ReportPath => _path;

    /// <summary>Writes the deterministic metadata header; counts, duration and completion time carry pending markers until <see cref="Finalize"/></summary>
    public void WriteHeader(string repositoryUrl, string branch, ReviewMode mode, string? baselineSha, string tipSha, DateTimeOffset startedAt,
        ReviewStrategy strategy = ReviewStrategy.Exhaustive)
    {
        _startedAt = startedAt;

        var builder = new StringBuilder();
        builder.AppendLine("# Informant Review Report");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| Run started | {startedAt:yyyy-MM-dd HH:mm:ss zzz} |");
        builder.AppendLine($"| Run completed | {PendingCompleted} |");
        builder.AppendLine($"| Run duration | {PendingDuration} |");
        builder.AppendLine($"| Repository | {repositoryUrl} |");
        builder.AppendLine($"| Branch | {branch} |");
        builder.AppendLine($"| Mode | {mode} |");
        builder.AppendLine($"| Strategy | {strategy} |");
        builder.AppendLine($"| Baseline SHA | {baselineSha ?? "(none; first-run candidate universe is the full tree)"} |");
        builder.AppendLine($"| Reviewed tip SHA | {tipSha} |");
        builder.AppendLine($"| Review model | {_modelName} |");
        builder.AppendLine($"| Model endpoint | {_modelEndpoint} |");
        builder.AppendLine($"| Configured fallback models | {_modelTargets.Count - 1} |");
        builder.AppendLine($"| Context budget | {_maxContextCharacters} characters |");
        builder.AppendLine($"| Max file lines before chunking | {_maxFileLines} |");
        if (_traceFileName is not null)
        {
            builder.AppendLine($"| Read and tool audit trace | {_traceFileName} |");
        }

        builder.AppendLine($"| Files reviewed | {PendingReviewed} |");
        builder.AppendLine($"| Files skipped or partial | {PendingSkipped} |");
        builder.AppendLine();
        builder.AppendLine("Findings are produced by the local review model, which cannot execute code; treat them as leads for human validation. "
            + "Structure, metadata and skip notes are written deterministically by Informant.");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        File.WriteAllText(_path, builder.ToString());
    }

    /// <summary>Appends one file section: findings, a skip note, or a partial result with both</summary>
    public void AppendFileSection(FileReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.AppendLine($"## {result.File.Path}");
        builder.AppendLine();
        builder.AppendLine($"Status: {result.File.Kind} | Changed line ranges: {FormatRanges(result.File.ChangedRanges)}");
        builder.AppendLine($"Review result: {result.Status}");
        if (result.ReviewModelName is not null)
        {
            builder.AppendLine($"Review model: {result.ReviewModelProfile ?? "primary"} (`{result.ReviewModelName}`)");
        }
        builder.AppendLine();

        if (result.TotalChunks > 1)
        {
            builder.AppendLine($"Parts reviewed: {result.CompletedChunks} of {result.TotalChunks} (file exceeded the size limit and was chunked at logical boundaries)");
            builder.AppendLine();
        }

        if (result.Findings is not null)
        {
            builder.AppendLine(result.Findings.TrimEnd());
            builder.AppendLine();
        }

        if (result.SkipReason is not null)
        {
            builder.AppendLine(result.Status switch
            {
                FileReviewStatus.NotReviewable => $"NOT REVIEWABLE: {result.SkipReason}",
                FileReviewStatus.Failed => $"FAILED: {result.SkipReason}",
                FileReviewStatus.Partial => $"PARTIAL: remainder not reviewed, {result.SkipReason}",
                FileReviewStatus.Deferred => $"DEFERRED: {result.SkipReason}",
                _ => result.SkipReason
            });
            builder.AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine();

        File.AppendAllText(_path, builder.ToString());
    }

    /// <summary>Appends the run summary, lists skipped or partial files with reasons, and patches the header counts and duration</summary>
    public void Finalize(int reviewedCount, IReadOnlyList<(string Path, string Reason)> skipped, TimeSpan duration, IReadOnlyList<PrimaryModelFailure>? modelFailures = null)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        var builder = new StringBuilder();
        builder.AppendLine("## Run summary");
        builder.AppendLine();
        builder.AppendLine($"Files reviewed in full: {reviewedCount}");
        builder.AppendLine();
        builder.AppendLine($"Files skipped or partial: {skipped.Count}");
        if (skipped.Count > 0)
        {
            builder.AppendLine();
            foreach ((string path, string reason) in skipped)
            {
                builder.AppendLine($"- {path}: {reason}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Primary model target failures: {modelFailures?.Count ?? 0}");
        if (modelFailures is { Count: > 0 })
        {
            builder.AppendLine();
            foreach (PrimaryModelFailure failure in modelFailures)
            {
                builder.AppendLine($"- {failure.Target.Name} (`{failure.Target.ModelName}`): {failure.Reason}");
            }
        }

        File.AppendAllText(_path, builder.ToString());

        // Patch only the header segment, so findings text that happened to contain a pending marker is never rewritten.
        // The delimiter is the standalone horizontal rule line; a bare "---" search would hit the table alignment row first
        string report = File.ReadAllText(_path);
        int headerEnd = report.IndexOf($"{Environment.NewLine}---{Environment.NewLine}", StringComparison.Ordinal);
        string header = headerEnd < 0 ? report : report[..headerEnd];
        header = header.Replace(PendingReviewed, reviewedCount.ToString());
        header = header.Replace(PendingSkipped, skipped.Count.ToString());
        header = header.Replace(PendingDuration, $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
        header = header.Replace(PendingCompleted, $"{_startedAt + duration:yyyy-MM-dd HH:mm:ss zzz}");
        File.WriteAllText(_path, headerEnd < 0 ? header : header + report[headerEnd..]);

        Log.Information("Report finalized: {Path}", _path);
    }

    /// <summary>Appends aggregate metadata-only trace counts after every optional review pass finishes</summary>
    public void AppendTraceSummary(ReviewTraceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine("## Read and tool audit trace");
        builder.AppendLine();
        builder.AppendLine($"Trace artifact: `{summary.FileName}`");
        builder.AppendLine();
        builder.Append($"Events: {summary.EventCount}; repository reads: {summary.ReadRequestCount} ");
        builder.AppendLine($"({summary.ServedReadCount} served, {summary.PartiallyServedReadCount} partial, {summary.RejectedReadCount} rejected); tool-call events: {summary.ToolCallEventCount}");
        File.AppendAllText(_path, builder.ToString());
    }

    /// <summary>Appends one completed or failed clustered review unit immediately so completed work survives a later process interruption</summary>
    public void AppendReviewUnitSection(ReviewUnitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.AppendLine($"## Review unit {result.Unit.Id}");
        builder.AppendLine();
        builder.AppendLine($"Review result: {(result.Succeeded ? "Reviewed" : "Failed")}");
        builder.AppendLine($"Rationale: {result.Unit.Rationale}");
        if (result.ReviewModelName is not null)
        {
            builder.AppendLine($"Review model: {result.ReviewModelProfile ?? "primary"} (`{result.ReviewModelName}`)");
        }

        builder.AppendLine();
        if (result.Succeeded)
        {
            foreach (ReviewUnitPartResult partResult in result.PartResults)
            {
                ReviewUnitPart part = partResult.Part;
                builder.AppendLine($"### {part.File.Path}, part {part.PartNumber} of {part.TotalParts}, lines {part.StartLine}-{part.EndLine}");
                builder.AppendLine();
                builder.AppendLine(partResult.Findings.Trim());
                builder.AppendLine();
            }
        }
        else
        {
            builder.AppendLine($"FAILED: {result.FailureReason ?? "clustered review failed without a reason"}");
            builder.AppendLine();
            builder.AppendLine("Affected source parts:");
            foreach (ReviewUnitPart part in result.Unit.Parts)
            {
                builder.AppendLine($"- {part.File.Path}, part {part.PartNumber} of {part.TotalParts}, lines {part.StartLine}-{part.EndLine}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine();
        File.AppendAllText(_path, builder.ToString());
    }

    /// <summary>Appends the metadata-only coverage ledger summary and names every adaptively deferred path</summary>
    public void AppendCoverageSummary(ReviewCoverageLedger coverage, string coverageFileName)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentException.ThrowIfNullOrWhiteSpace(coverageFileName);

        var builder = new StringBuilder();
        builder.AppendLine("## Review coverage");
        builder.AppendLine();
        builder.AppendLine($"Coverage artifact: `{coverageFileName}`");
        builder.AppendLine();
        builder.AppendLine($"Strategy: {coverage.Strategy}");
        builder.AppendLine();
        builder.AppendLine($"Selected for deep review: {coverage.SelectedCount}");
        builder.AppendLine();
        builder.AppendLine($"Deep reviewed: {coverage.DeepReviewedCount}");
        builder.AppendLine();
        builder.AppendLine($"Mandatory changed content reviewed after deep-review deferral: {coverage.MandatoryChangesReviewedCount}");
        builder.AppendLine();
        builder.AppendLine($"Deferred without mandatory changed content: {coverage.DeferredCount}");
        builder.AppendLine();
        builder.AppendLine($"Excluded: {coverage.ExcludedCount}; partial: {coverage.PartialCount}; failed: {coverage.FailedCount}");
        builder.AppendLine();
        if (coverage.Strategy == ReviewStrategy.Adaptive)
        {
            builder.AppendLine("Adaptive coverage does not claim every file was deeply reviewed. Deferred files may contain issues the selected review units did not expose.");
            builder.AppendLine();
        }

        ReviewCoverageEntry[] deferred = [.. coverage.Entries.Where(entry => entry.DeepReviewDeferred)];
        if (deferred.Length > 0)
        {
            builder.AppendLine("Deep-review deferrals:");
            foreach (ReviewCoverageEntry entry in deferred)
            {
                builder.AppendLine($"- {entry.Path}: {entry.Outcome}{(string.IsNullOrWhiteSpace(entry.Reason) ? "" : $"; {entry.Reason}")}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine();
        File.AppendAllText(_path, builder.ToString());
    }

    private static string FormatRanges(IReadOnlyList<LineRange> ranges) => ranges.Count == 0 ? "(none)" : string.Join(", ", ranges.Select(range => range.ToString()));
}
