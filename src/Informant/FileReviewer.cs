using System.Text;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Final disposition of one file in the primary review</summary>
public enum FileReviewStatus
{
    /// <summary>Every reviewable part completed</summary>
    Reviewed,

    /// <summary>The file was intentionally excluded because its content cannot or need not be reviewed</summary>
    NotReviewable,

    /// <summary>No review completed because an unexpected read or model failure occurred</summary>
    Failed,

    /// <summary>Some, but not all, reviewable parts completed</summary>
    Partial
}

/// <summary>Outcome of reviewing one file, with an explicit status separating expected exclusions from failures that must preserve the previous baseline</summary>
public sealed record FileReviewResult(ChangedFile File, FileReviewStatus Status, string? Findings, int CompletedChunks, int TotalChunks, string? SkipReason)
{
    /// <summary>True when every part of the file was reviewed</summary>
    public bool FullyReviewed => Status == FileReviewStatus.Reviewed;
}

/// <summary>Decides whether a completed primary pass is safe to record as the new baseline</summary>
public static class ReviewCompletion
{
    /// <summary>Returns true when every changed file was reviewed or intentionally classified as not reviewable</summary>
    public static bool CanAdvanceBaseline(IEnumerable<FileReviewResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.All(result => result.Status is FileReviewStatus.Reviewed or FileReviewStatus.NotReviewable);
    }
}

/// <summary>Reviews one file at a time: whole-file feeding with changed-line focus hints, logical chunking for oversized files, and retry-then-skip resilience so one bad file never kills the run</summary>
public sealed class FileReviewer
{
    private readonly ToolCallLoop _loop;
    private readonly RepositoryFileReader _fileReader;
    private readonly GitRunner? _git;
    private readonly string _treeRoot;
    private readonly string _systemPrompt;
    private readonly int _maxFileLines;
    private readonly int _maxContentCharacters;
    private readonly int _retryCount;
    private readonly int _maxFileBytes;

    /// <summary>Creates a reviewer; content per call is capped at half the context budget, leaving the rest for the prompt and on-demand tool reads</summary>
    public FileReviewer(ToolCallLoop loop, string treeRoot, string systemPrompt, int maxFileLines, int maxContextCharacters, int retryCount, int maxFileBytes = RepositoryFileReader.DefaultMaxFileBytes,
        GitRunner? git = null)
    {
        ArgumentNullException.ThrowIfNull(loop);

        _loop = loop;
        _fileReader = new RepositoryFileReader(treeRoot, maxFileBytes);
        _git = git;
        _treeRoot = treeRoot;
        _systemPrompt = systemPrompt;
        _maxFileLines = maxFileLines;
        _maxContentCharacters = Math.Max(2000, maxContextCharacters / 2);
        _retryCount = retryCount;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>Reviews the file and returns findings, a skip, or a partial result; never throws for per-file failures</summary>
    public async Task<FileReviewResult> ReviewAsync(ChangedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Kind is ChangeKind.Modified or ChangeKind.Renamed && file.ChangedRanges.Count == 0)
        {
            return NotReviewable(file, "no line changes (pure rename or metadata-only change)");
        }

        string[] lines;
        try
        {
            lines = file.Kind == ChangeKind.Deleted
                ? await ReadDeletedLinesAsync(file)
                : await _fileReader.ReadAllLinesAsync(file.Path, cancellationToken);
        }
        catch (RepositoryFileException ex)
        {
            string reason = ex.Error == RepositoryFileError.NotFound ? "file not present in the working tree" : ex.Message;
            return IsExpectedExclusion(ex.Error) ? NotReviewable(file, reason) : Failed(file, reason);
        }

        if (lines.Length == 0)
        {
            return NotReviewable(file, "empty file");
        }

        if (Array.Exists(lines, line => line.Contains('\0')))
        {
            return NotReviewable(file, "binary file");
        }

        IReadOnlyList<SourceChunk> chunks = SourceChunker.Split(lines, _maxFileLines, _maxContentCharacters);
        var findings = new StringBuilder();

        for (int index = 0; index < chunks.Count; index++)
        {
            string userPrompt = BuildUserPrompt(file, lines, chunks[index], index + 1, chunks.Count);

            string? partFindings = await RunWithRetriesAsync(file, index + 1, chunks.Count, userPrompt, cancellationToken);
            if (partFindings is null)
            {
                string reason = $"part {index + 1} of {chunks.Count} failed after {_retryCount} retries";
                FileReviewStatus status = index == 0 ? FileReviewStatus.Failed : FileReviewStatus.Partial;
                return new FileReviewResult(file, status, findings.Length > 0 ? findings.ToString() : null, index, chunks.Count, reason);
            }

            if (chunks.Count > 1)
            {
                findings.AppendLine($"### Part {index + 1} of {chunks.Count} (lines {chunks[index].StartLine}-{chunks[index].EndLine})");
                findings.AppendLine();
            }

            findings.AppendLine(partFindings.Trim());
            findings.AppendLine();
        }

        return new FileReviewResult(file, FileReviewStatus.Reviewed, findings.ToString().TrimEnd() + Environment.NewLine, chunks.Count, chunks.Count, null);
    }

    private async Task<string?> RunWithRetriesAsync(ChangedFile file, int part, int totalParts, string userPrompt, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt <= _retryCount; attempt++)
        {
            try
            {
                var result = await _loop.RunAsync(_systemPrompt, userPrompt, cancellationToken);
                Log.Information("Reviewed {Path} part {Part}/{Total}: {ToolCalls} tool calls, {Characters} finding characters", file.Path, part, totalParts, result.ToolCallCount, result.FinalContent.Length);
                return result.FinalContent;
            }
            catch (ModelCallException ex)
            {
                Log.Warning("Review of {Path} part {Part}/{Total} attempt {Attempt}/{Attempts} failed: {Reason}", file.Path, part, totalParts, attempt + 1, _retryCount + 1, ex.Message);
            }
        }

        return null;
    }

    private static FileReviewResult NotReviewable(ChangedFile file, string reason)
    {
        Log.Information("Not reviewing {Path}: {Reason}", file.Path, reason);
        return new FileReviewResult(file, FileReviewStatus.NotReviewable, null, 0, 0, reason);
    }

    private static FileReviewResult Failed(ChangedFile file, string reason)
    {
        Log.Warning("Review failed for {Path}: {Reason}", file.Path, reason);
        return new FileReviewResult(file, FileReviewStatus.Failed, null, 0, 0, reason);
    }

    private static bool IsExpectedExclusion(RepositoryFileError error) => error is RepositoryFileError.ReparsePoint or RepositoryFileError.TooLarge or RepositoryFileError.Binary;

    private static string BuildUserPrompt(ChangedFile file, string[] lines, SourceChunk chunk, int part, int totalParts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Review the following file.");
        builder.AppendLine();
        builder.AppendLine($"File: {file.Path}");
        builder.AppendLine($"Change status: {DescribeKind(file.Kind)}");

        LineRange[] rangesInChunk = [.. file.ChangedRanges.Where(range => range.End >= chunk.StartLine && range.Start <= chunk.EndLine)];
        switch (file.Kind)
        {
            case ChangeKind.Added:
                builder.AppendLine("The file is newly added; the entire file is the review subject.");
                break;

            case ChangeKind.FullReview:
                builder.AppendLine("Full-tree review; the entire file is the review subject.");
                break;

            case ChangeKind.Deleted:
                builder.AppendLine("The file was deleted; the content below is its complete baseline version before removal.");
                builder.AppendLine("Review whether removing it breaks surviving references, builds, configuration, documentation, deployment, compatibility, or expected behavior.");
                builder.AppendLine("Report obsolete code only when its removal is incomplete or harmful.");
                break;

            default:
                builder.AppendLine(rangesInChunk.Length > 0
                    ? $"Changed line ranges (1-based, inclusive): {string.Join(", ", rangesInChunk.Select(range => range.ToString()))}. Review those lines; the rest of the file is context."
                    : "No changed lines fall in this part; it is context for the parts that do. Only report serious defects here.");
                break;
        }

        if (totalParts > 1)
        {
            builder.AppendLine($"This is part {part} of {totalParts} of the file, covering lines {chunk.StartLine} to {chunk.EndLine}. Other parts are reviewed separately; use read_file_lines to see them if needed.");
            if (chunk.HardCut)
            {
                builder.AppendLine("Note: this part ends at a size limit rather than a clean code boundary.");
            }
        }

        builder.AppendLine();
        builder.AppendLine("File content (line numbers prefixed):");
        for (int line = chunk.StartLine; line <= chunk.EndLine; line++)
        {
            builder.AppendLine($"{line,6} | {lines[line - 1]}");
        }

        builder.AppendLine();
        builder.AppendLine("Provide your review findings for this file now.");
        return builder.ToString();
    }

    private static string DescribeKind(ChangeKind kind)
    {
        switch (kind)
        {
            case ChangeKind.Added:
                return "added";
            
            case ChangeKind.Modified:
                return "modified";
            
            case ChangeKind.Renamed:
                return "renamed (with content edits)";

            case ChangeKind.Deleted:
                return "deleted (baseline content shown)";
            
            case ChangeKind.FullReview:
                return "full-tree review";
            
            default:
                return kind.ToString();
        }
    }

    private async Task<string[]> ReadDeletedLinesAsync(ChangedFile file)
    {
        if (_git is null || string.IsNullOrWhiteSpace(file.ContentRevision))
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"deleted file '{file.Path}' has no baseline Git revision available");
        }

        return await RepositoryFileReader.ReadGitBlobLinesAsync(_git, _treeRoot, file.ContentRevision, file.Path, _maxFileBytes);
    }
}
