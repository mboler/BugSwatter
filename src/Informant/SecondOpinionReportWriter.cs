using System.Text;
using Serilog;

namespace Informant;

/// <summary>Writes the validated report produced by the second-opinion pass as its own artifact next to the original; the local report is never modified. Structure and metadata are deterministic, validation text comes verbatim from the frontier model</summary>
public sealed class SecondOpinionReportWriter
{
    private const string PendingValidated = "(pending: files validated)";
    private const string PendingFailed = "(pending: files failed)";
    private const string PendingBudgetDeferred = "(pending: files deferred by budget)";
    private const string PendingDuration = "(pending: pass duration)";
    private const string PendingCompleted = "(pending: pass completed)";

    private readonly string _path;
    private DateTimeOffset _startedAt;

    /// <summary>Creates the validated-report path for this run, alongside the original report</summary>
    public SecondOpinionReportWriter(string directory, string runStamp)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"Informant-Report-{runStamp}-validated.md");
    }

    /// <summary>Full path of the validated report</summary>
    public string ReportPath => _path;

    /// <summary>Writes the deterministic header; counts, duration and completion time carry pending markers until <see cref="Finalize"/></summary>
    public void WriteHeader(SecondOpinionModelSelection selection, string sourceReportPath, DateTimeOffset startedAt, int contextLines, SecondOpinionScope scope = SecondOpinionScope.AllReviewed,
        int selectedCount = 0, int candidateCount = 0, int cleanSampleCount = 0, int? reviewBudgetMinutes = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _startedAt = startedAt;

        var builder = new StringBuilder();
        builder.AppendLine("# Informant Second Opinion (validated review)");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| Pass started | {startedAt:yyyy-MM-dd HH:mm:ss zzz} |");
        builder.AppendLine($"| Pass completed | {PendingCompleted} |");
        builder.AppendLine($"| Pass duration | {PendingDuration} |");
        builder.AppendLine($"| Validating model | {selection.Model.ModelName} |");
        builder.AppendLine($"| Model profile | {selection.ProfileName} |");
        builder.AppendLine($"| Endpoint | {selection.Model.Endpoint} |");
        builder.AppendLine($"| Primary candidate severity | {selection.PrimaryClassification.DisplaySeverity} |");
        builder.AppendLine($"| Selection reason | {selection.SelectionReason} |");
        builder.AppendLine($"| Context window | {contextLines} lines around each change |");
        builder.AppendLine($"| Validation scope | {scope} |");
        builder.AppendLine($"| Selected primary results | {selectedCount} ({candidateCount} with candidates, {cleanSampleCount} clean samples) |");
        builder.AppendLine($"| Pass budget | {(reviewBudgetMinutes is null ? "unbounded" : $"{reviewBudgetMinutes} minutes")} |");
        builder.AppendLine($"| Source report | {Path.GetFileName(sourceReportPath)} |");
        builder.AppendLine($"| Files validated | {PendingValidated} |");
        builder.AppendLine($"| Files failed | {PendingFailed} |");
        builder.AppendLine($"| Files deferred by budget | {PendingBudgetDeferred} |");
        builder.AppendLine();
        builder.AppendLine("Each section below is the second-opinion model's validation of the local reviewer's findings against the actual code: confirmed findings with calibrated severity, discarded findings with the reason, and a verdict. The original local report stands unmodified alongside this one.");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        File.WriteAllText(_path, builder.ToString());
    }

    /// <summary>Appends one file's completed validation</summary>
    public void AppendFileSection(string filePath, IReadOnlyList<LineRange> ranges, string validationText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationText);
        AppendSection(filePath, ranges, validationText.Trim());
    }

    /// <summary>Appends one file's explicit incomplete-validation status, reason and optional model response</summary>
    public void AppendFailureSection(string filePath, IReadOnlyList<LineRange> ranges, SecondOpinionValidationStatus status, string reason, string? responseText = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!Enum.IsDefined(status) || status == SecondOpinionValidationStatus.Validated)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A failure section must use a failure status");
        }

        var content = new StringBuilder();
        content.AppendLine($"VALIDATION INCOMPLETE ({status}): {reason}");
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            content.AppendLine();
            content.AppendLine("Model response:");
            content.Append(responseText.Trim());
        }

        AppendSection(filePath, ranges, content.ToString());
    }

    /// <summary>Patches the header counts and duration; the delimiter is the standalone horizontal rule, not the table alignment row</summary>
    public void Finalize(int validatedCount, int failedCount, TimeSpan duration, int budgetDeferredCount = 0)
    {
        string report = File.ReadAllText(_path);
        int headerEnd = report.IndexOf($"{Environment.NewLine}---{Environment.NewLine}", StringComparison.Ordinal);
        string header = headerEnd < 0 ? report : report[..headerEnd];
        header = header.Replace(PendingValidated, validatedCount.ToString());
        header = header.Replace(PendingFailed, failedCount.ToString());
        header = header.Replace(PendingBudgetDeferred, budgetDeferredCount.ToString());
        header = header.Replace(PendingDuration, $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
        header = header.Replace(PendingCompleted, $"{_startedAt + duration:yyyy-MM-dd HH:mm:ss zzz}");
        File.WriteAllText(_path, headerEnd < 0 ? header : header + report[headerEnd..]);

        Log.Information("Validated report finalized: {Path}", _path);
    }

    private void AppendSection(string filePath, IReadOnlyList<LineRange> ranges, string content)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {filePath}");
        builder.AppendLine();
        builder.AppendLine($"Changed line ranges: {(ranges.Count == 0 ? "(entire file)" : string.Join(", ", ranges.Select(range => range.ToString())))}");
        builder.AppendLine();
        builder.AppendLine(content);
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        File.AppendAllText(_path, builder.ToString());
    }
}
