using System.Text;
using Serilog;

namespace Informant;

/// <summary>Runs the second-opinion validation for one reviewed file: the local model's findings are sent to the validating model together with the referenced code read fresh from the working tree, so every claim is checked against ground truth rather than rubber-stamped. When the endpoint supports tool-calling the model is also offered the read-only read_file_lines tool, confined to the working tree, so it can pull more of the file on demand; when it does not, validation runs from the code excerpt alone. With the tool enabled a cloud endpoint can read further lines of the working tree on its own initiative</summary>
public sealed class SecondOpinionReviewer
{
    private readonly ModelClient _client;
    private readonly string _treeRoot;
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
    public SecondOpinionReviewer(ModelClient client, string treeRoot, string systemPrompt, int maxContextCharacters, int contextLines, bool enableToolCalls, int maxFileReads)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _treeRoot = treeRoot;
        _systemPrompt = systemPrompt;
        _maxContentCharacters = Math.Max(2000, maxContextCharacters / 2);
        _contextLines = contextLines;

        // When the validating endpoint supports tool-calling, give it the same read-only file tool the local reviewer
        // uses, confined to the working tree and capped at maxFileReads so it cannot read the whole tree. Otherwise the
        // loop is null and the validation runs from the excerpt alone
        _toolLoop = enableToolCalls ? new ToolCallLoop(client, new ReadFileLinesTool(treeRoot), maxContextCharacters, maxFileReads) : null;
    }

    /// <summary>Validates one file's local findings against its code; returns the validation text, or null when the call failed (logged, never thrown)</summary>
    public async Task<string?> ValidateAsync(FileReviewResult localResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localResult);

        try
        {
            string userPrompt = BuildUserPrompt(localResult);

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

        int totalCharacters = lines.Sum(line => line.Length + 10);
        if (totalCharacters <= maxCharacters || ranges.Count == 0)
        {
            return NumberLines(lines, 1, lines.Length, maxCharacters);
        }

        // Widen each changed range by the context margin, then merge overlapping windows so nothing prints twice
        var windows = new List<(int Start, int End)>();
        foreach (var range in ranges.OrderBy(range => range.Start))
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
            if (builder.Length > 0)
            {
                builder.AppendLine("... (lines omitted) ...");
            }

            builder.AppendLine(NumberLines(lines, start, end, maxCharacters - builder.Length).TrimEnd());
        }

        return builder.ToString();
    }

    private static string NumberLines(string[] lines, int start, int end, int maxCharacters)
    {
        var builder = new StringBuilder();
        for (int line = start; line <= end; line++)
        {
            if (builder.Length > maxCharacters)
            {
                builder.AppendLine($"... (truncated at the context budget; {end - line + 1} lines not shown) ...");
                break;
            }

            builder.AppendLine($"{line,6} | {lines[line - 1]}");
        }

        return builder.ToString();
    }

    private string BuildUserPrompt(FileReviewResult localResult)
    {
        string excerpt;
        try
        {
            string[] lines = File.ReadAllLines(Path.Combine(_treeRoot, localResult.File.Path));
            excerpt = BuildCodeExcerpt(lines, localResult.File.ChangedRanges, _maxContentCharacters, _contextLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            builder.AppendLine("You may call the read_file_lines tool to read more of this file if the excerpt is not enough, for example to check something the local reviewer may have missed. Use it sparingly and stay within this file; do not try to read the whole tree.");
            builder.AppendLine();
        }

        builder.AppendLine("Produce your CONFIRMED FINDINGS, DISCARDED FINDINGS and VERDICT now.");

        return builder.ToString();
    }
}
