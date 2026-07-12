using System.Text;
using Serilog;

namespace SlimShady;

/// <summary>Writes the validated report produced by the second-opinion pass as its own artifact next to the original; the local report is never modified. Structure and metadata are deterministic, validation text comes verbatim from the frontier model</summary>
public sealed class SecondOpinionReportWriter
{
    private const string PendingValidated = "(pending: files validated)";
    private const string PendingFailed = "(pending: files failed)";
    private const string PendingDuration = "(pending: pass duration)";
    private const string PendingCompleted = "(pending: pass completed)";

    private readonly string _path;
    private DateTimeOffset _startedAt;

    /// <summary>Creates the validated-report path for this run, alongside the original report</summary>
    public SecondOpinionReportWriter(string directory, string runStamp)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"SlimShady-Report-{runStamp}-validated.md");
    }

    /// <summary>Full path of the validated report</summary>
    public string ReportPath => _path;

    /// <summary>Writes the deterministic header; counts, duration and completion time carry pending markers until <see cref="Finalize"/></summary>
    public void WriteHeader(string modelName, string endpoint, string sourceReportPath, DateTimeOffset startedAt, int contextLines)
    {
        _startedAt = startedAt;

        var builder = new StringBuilder();
        builder.AppendLine("# SlimShady Second Opinion (validated review)");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        builder.AppendLine($"| Pass started | {startedAt:yyyy-MM-dd HH:mm:ss zzz} |");
        builder.AppendLine($"| Pass completed | {PendingCompleted} |");
        builder.AppendLine($"| Pass duration | {PendingDuration} |");
        builder.AppendLine($"| Validating model | {modelName} |");
        builder.AppendLine($"| Endpoint | {endpoint} |");
        builder.AppendLine($"| Context window | {contextLines} lines around each change |");
        builder.AppendLine($"| Source report | {Path.GetFileName(sourceReportPath)} |");
        builder.AppendLine($"| Files validated | {PendingValidated} |");
        builder.AppendLine($"| Files failed | {PendingFailed} |");
        builder.AppendLine();
        builder.AppendLine("Each section below is the second-opinion model's validation of the local reviewer's findings against the actual code: confirmed findings with calibrated severity, discarded findings with the reason, and a verdict. The original local report stands unmodified alongside this one.");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        File.WriteAllText(_path, builder.ToString());
    }

    /// <summary>Appends one file's validation, or a failure note when the call did not produce one</summary>
    public void AppendFileSection(string filePath, IReadOnlyList<LineRange> ranges, string? validationText)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"## {filePath}");
        builder.AppendLine();
        builder.AppendLine($"Changed line ranges: {(ranges.Count == 0 ? "(entire file)" : string.Join(", ", ranges.Select(range => range.ToString())))}");
        builder.AppendLine();

        builder.AppendLine(validationText is null
            ? "VALIDATION FAILED: the second-opinion call for this file did not succeed; see the log. The local findings for this file stand unvalidated"
            : validationText.Trim());

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        File.AppendAllText(_path, builder.ToString());
    }

    /// <summary>Patches the header counts and duration; the delimiter is the standalone horizontal rule, not the table alignment row</summary>
    public void Finalize(int validatedCount, int failedCount, TimeSpan duration)
    {
        string report = File.ReadAllText(_path);
        int headerEnd = report.IndexOf($"{Environment.NewLine}---{Environment.NewLine}", StringComparison.Ordinal);
        string header = headerEnd < 0 ? report : report[..headerEnd];
        header = header.Replace(PendingValidated, validatedCount.ToString());
        header = header.Replace(PendingFailed, failedCount.ToString());
        header = header.Replace(PendingDuration, $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}");
        header = header.Replace(PendingCompleted, $"{_startedAt + duration:yyyy-MM-dd HH:mm:ss zzz}");
        File.WriteAllText(_path, headerEnd < 0 ? header : header + report[headerEnd..]);

        Log.Information("Validated report finalized: {Path}", _path);
    }
}
