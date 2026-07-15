using System.Text;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Runs the second-opinion validation for one reviewed file: the local model's findings are sent to the validating model together with the referenced code read fresh from the working tree, so every claim is checked against ground truth rather than rubber-stamped. When the endpoint supports tool-calling the model is also offered the read-only read_file_lines tool, confined to the working tree, so it can pull more of the file on demand; when it does not, validation runs from the code excerpt alone. With the tool enabled a cloud endpoint can read further lines of the working tree on its own initiative</summary>
public sealed class SecondOpinionReviewer
{
    private readonly ModelClient _client;
    private readonly RepositoryFileReader _fileReader;
    private readonly GitRunner? _git;
    private readonly string _treeRoot;
    private readonly int _maxFileBytes;
    private readonly string _systemPrompt;
    private readonly int _maxContentCharacters;
    private readonly int _contextLines;
    private readonly ToolCallLoop? _toolLoop;

    /// <summary>Creates a reviewer; code excerpts are capped at half the context budget, matching the local pass</summary>
    /// <param name="client">Client for the validating endpoint, already carrying its API key when the endpoint needs one</param>
    /// <param name="treeRoot">Working-tree root the referenced code is read from</param>
    /// <param name="systemPrompt">Resolved validation prompt sent as the system message</param>
    /// <param name="maxContextCharacters">Overall context budget; the code excerpt is capped at half of it</param>
    /// <param name="contextLines">Lines of surrounding code included on each side of a changed range when the whole file exceeds the budget</param>
    /// <param name="enableToolCalls">When true the validating model is offered read_file_lines to read more of the file on demand; when false it validates from the excerpt only</param>
    /// <param name="maxFileReads">Cap on read_file_lines calls per file when tools are enabled, so a capable model cannot pull the whole tree</param>
    /// <param name="maxFileBytes">Maximum permitted live repository file size</param>
    /// <param name="git">Optional Git runner used to read deleted baseline content</param>
    /// <param name="manifest">Current run manifest used as the model read allowlist</param>
    /// <param name="maxToolResultCharacters">Optional serialized tool-result character limit</param>
    /// <param name="readAuditObserver">Optional metadata-only repository-read observer</param>
    /// <param name="toolAuditObserver">Optional metadata-only generic tool-call observer</param>
    public SecondOpinionReviewer(ModelClient client, string treeRoot, string systemPrompt, int maxContextCharacters, int contextLines, bool enableToolCalls, int maxFileReads,
        int maxFileBytes = RepositoryFileReader.DefaultMaxFileBytes, GitRunner? git = null, RepositoryManifest? manifest = null, int? maxToolResultCharacters = null,
        Action<RepositoryReadAuditEvent>? readAuditObserver = null, Action<ModelToolCallAuditEvent>? toolAuditObserver = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _fileReader = new RepositoryFileReader(treeRoot, maxFileBytes);
        _git = git;
        _treeRoot = treeRoot;
        _maxFileBytes = maxFileBytes;
        _systemPrompt = systemPrompt;
        _maxContentCharacters = Math.Max(2000, maxContextCharacters / 2);
        _contextLines = contextLines;

        // When the validating endpoint supports tool-calling, give it the same read-only file tool the local reviewer
        // uses, confined to the working tree and capped at maxFileReads so it cannot read the whole tree. Otherwise the
        // loop is null and the validation runs from the excerpt alone
        if (enableToolCalls)
        {
            int toolResultCharacters = maxToolResultCharacters ?? ReadFileLinesTool.ResultCharactersForContext(maxContextCharacters);
            _toolLoop = new ToolCallLoop(client, new ReadFileLinesTool(_fileReader, manifest, toolResultCharacters, readAuditObserver), maxContextCharacters, maxFileReads, toolAuditObserver);
        }
    }

    /// <summary>Validates one file's local findings against its code; returns the validation text, or null when the call failed (logged, never thrown)</summary>
    public async Task<string?> ValidateAsync(FileReviewResult localResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localResult);

        try
        {
            string userPrompt = await BuildUserPromptAsync(localResult, cancellationToken);

            string? content;
            if (_toolLoop is not null)
            {
                LoopResult result = await _toolLoop.RunAsync(_systemPrompt, userPrompt, cancellationToken);
                content = result.FinalContent;
            }
            else
            {
                var reply = await _client.CompleteAsync([new ChatMessage { Role = "system", Content = _systemPrompt }, new ChatMessage { Role = "user", Content = userPrompt }], [], cancellationToken);
                content = reply.Content;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Warning("Second opinion for {Path} returned an empty answer", localResult.File.Path);
                return null;
            }

            return content;
        }
        catch (ModelCallException ex)
        {
            Log.Warning("Second opinion for {Path} failed: {Reason}", localResult.File.Path, ex.Message);
            return null;
        }
    }

    /// <summary>Builds the numbered code excerpt: the whole file when it fits the budget, otherwise the changed ranges widened by <paramref name="contextLines"/> with elision markers between windows</summary>
    /// <param name="lines">The full source file, one entry per line</param>
    /// <param name="ranges">The changed line ranges that must appear in the excerpt</param>
    /// <param name="maxCharacters">Character budget for the excerpt; the whole file is used when it fits, otherwise only windows around the ranges</param>
    /// <param name="contextLines">Lines of surrounding code kept on each side of a changed range</param>
    /// <returns>Numbered source text ready to embed in the validation prompt</returns>
    public static string BuildCodeExcerpt(string[] lines, IReadOnlyList<LineRange> ranges, int maxCharacters, int contextLines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);

        long totalCharacters = lines.Sum(line => (long)line.Length + 10);
        if (totalCharacters <= maxCharacters || ranges.Count == 0)
        {
            return NumberLines(lines, 1, lines.Length, maxCharacters);
        }

        // Widen each changed range by the context margin, then merge overlapping windows so nothing prints twice
        var windows = new List<(int Start, int End)>();
        foreach (LineRange range in ranges.OrderBy(range => range.Start))
        {
            int start = Math.Max(1, range.Start - contextLines);
            int end = Math.Min(lines.Length, range.End + contextLines);

            if (windows.Count > 0 && start <= windows[^1].End + 1)
            {
                windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, end));
            }
            else
            {
                windows.Add((start, end));
            }
        }

        var builder = new StringBuilder();
        foreach ((int start, int end) in windows)
        {
            const string Omitted = "... (lines omitted) ...\n";
            if (builder.Length > 0)
            {
                if (builder.Length + Omitted.Length > maxCharacters)
                {
                    break;
                }

                builder.Append(Omitted);
            }

            string window = NumberLines(lines, start, end, maxCharacters - builder.Length);
            if (window.Length == 0)
            {
                break;
            }

            builder.Append(window);
            if (builder.Length == maxCharacters)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static string NumberLines(string[] lines, int start, int end, int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        for (int line = start; line <= end; line++)
        {
            string numberedLine = $"{line,6} | {lines[line - 1]}\n";
            if (builder.Length + numberedLine.Length <= maxCharacters)
            {
                builder.Append(numberedLine);
                continue;
            }

            AppendTruncation(builder, end - line + 1, maxCharacters);
            break;
        }

        return builder.ToString();
    }

    private static void AppendTruncation(StringBuilder builder, int omittedLineCount, int maxCharacters)
    {
        string marker = $"... (truncated at the context budget; {omittedLineCount} lines not shown) ...\n";
        int remaining = maxCharacters - builder.Length;
        if (remaining > 0)
        {
            builder.Append(marker.AsSpan(0, Math.Min(marker.Length, remaining)));
        }
    }

    private async Task<string> BuildUserPromptAsync(FileReviewResult localResult, CancellationToken cancellationToken)
    {
        string excerpt;
        try
        {
            string[] lines = localResult.File.Kind == ChangeKind.Deleted
                ? await ReadDeletedLinesAsync(localResult.File, cancellationToken)
                : _fileReader.ReadAllLines(localResult.File.Path);
            excerpt = BuildCodeExcerpt(lines, localResult.File.ChangedRanges, _maxContentCharacters, _contextLines);
        }
        catch (RepositoryFileException ex)
        {
            excerpt = $"(the code could not be read: {ex.Message}; judge only what is verifiable without it and mark the rest UNVERIFIABLE)";
        }

        bool localReviewed = localResult.Findings is not null;
        var builder = new StringBuilder();

        builder.AppendLine(localReviewed
            ? "Validate the local reviewer's findings for the following file, and note any real issue it missed."
            : $"The local reviewer could not review the following file{(localResult.SkipReason is null ? "" : $" (reason: {localResult.SkipReason})")}. Review the changed lines yourself and report any real issues.");
        builder.AppendLine();
        builder.AppendLine($"File: {localResult.File.Path}");
        builder.AppendLine($"Changed line ranges: {(localResult.File.ChangedRanges.Count == 0 ? "(entire file)" : string.Join(", ", localResult.File.ChangedRanges.Select(range => range.ToString())))}");
        builder.AppendLine();

        if (localReviewed)
        {
            builder.AppendLine("Local reviewer findings:");
            builder.AppendLine(localResult.Findings!.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("Actual code (line numbers prefixed, the ground truth):");
        builder.AppendLine(excerpt.TrimEnd());
        builder.AppendLine();

        if (_toolLoop is not null)
        {
            if (localResult.File.Kind == ChangeKind.Deleted)
            {
                builder.AppendLine("You may call read_file_lines sparingly to inspect surviving files that may still reference or depend on this deleted file.");
                builder.AppendLine("The deleted path itself no longer exists in the working tree.");
            }
            else
            {
                builder.AppendLine("You may call read_file_lines to read more of this file if the excerpt is not enough, for example to check something the local reviewer may have missed.");
                builder.AppendLine("Use it sparingly and stay within this file; do not try to read the whole tree.");
            }

            builder.AppendLine();
        }

        builder.AppendLine("Produce your CONFIRMED FINDINGS, DISCARDED FINDINGS and VERDICT now.");

        return builder.ToString();
    }

    private async Task<string[]> ReadDeletedLinesAsync(ChangedFile file, CancellationToken cancellationToken)
    {
        if (_git is null || string.IsNullOrWhiteSpace(file.ContentRevision))
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"deleted file '{file.Path}' has no baseline Git revision available");
        }

        return await GitBlobReader.ReadLinesAsync(_git, _treeRoot, file.ContentRevision, file.Path, _maxFileBytes, cancellationToken);
    }
}
