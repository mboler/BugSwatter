using System.Text.RegularExpressions;
using Serilog;

namespace SlimShady;

/// <summary>One parsed line of git diff --name-status output</summary>
public sealed record NameStatusEntry(ChangeKind Kind, string Path, string? OldPath);

/// <summary>Determines what changed between the baseline and the branch tip: the file set from --name-status, and per file the changed line ranges from unified-diff hunk headers</summary>
public sealed partial class ChangeDetector
{
    private readonly GitRunner _git;
    private readonly string _treePath;

    /// <summary>Creates a detector operating on the given working tree</summary>
    public ChangeDetector(GitRunner git, string treePath)
    {
        ArgumentNullException.ThrowIfNull(git);
        
        _git = git;
        _treePath = treePath;
    }

    /// <summary>Returns true when <paramref name="sha"/> still names a commit in the tree; a rewritten branch can orphan the recorded baseline</summary>
    public async Task<bool> IsCommitReachableAsync(string sha)
    {
        // The doubled braces are C# escapes: this renders as <sha>^{commit}, git's peel-to-commit revision syntax
        var result = await _git.RunAsync("-C", _treePath, "cat-file", "-e", $"{sha}^{{commit}}");
        
        return result.ExitCode == 0;
    }

    /// <summary>Lists reviewable files changed between the two commits, each with its changed line ranges on the new side</summary>
    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baselineSha, string tipSha)
    {
        string nameStatus = await _git.RunCheckedAsync("-C", _treePath, "diff", "--name-status", baselineSha, tipSha);

        var files = new List<ChangedFile>();
        foreach (var entry in ParseNameStatus(nameStatus))
        {
            // -U0 makes hunk headers bracket exactly the changed lines, with no context rows mixed in.
            // For renames both paths go into the pathspec so git can pair them and diff only the content edits.
            // :(literal) disables pathspec globbing, otherwise a filename like data[1].cs would match data1.cs instead of itself
            string[] diffArguments = entry.OldPath is null
                ? ["-C", _treePath, "diff", "-U0", baselineSha, tipSha, "--", $":(literal){entry.Path}"]
                : ["-C", _treePath, "diff", "-U0", baselineSha, tipSha, "--", $":(literal){entry.OldPath}", $":(literal){entry.Path}"];
            string diff = await _git.RunCheckedAsync(diffArguments);

            files.Add(new ChangedFile(entry.Path, entry.Kind, ParseHunkRanges(diff)));
        }

        Log.Information("Change detection found {Count} reviewable changed files between {Baseline} and {Tip}", files.Count, baselineSha, tipSha);
        return files;
    }

    /// <summary>Lists every tracked file in the tree for a full review</summary>
    public async Task<IReadOnlyList<ChangedFile>> GetAllFilesAsync()
    {
        string output = await _git.RunCheckedAsync("-C", _treePath, "ls-files");
        
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => new ChangedFile(line.Trim(), ChangeKind.FullReview, []))];
    }

    /// <summary>Parses git diff --name-status output into reviewable entries; deleted files are skipped because there is nothing left to review</summary>
    public static IReadOnlyList<NameStatusEntry> ParseNameStatus(string output)
    {
        var entries = new List<NameStatusEntry>();

        foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = rawLine.TrimEnd('\r').Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            switch (parts[0][0])
            {
                case 'A':
                    entries.Add(new NameStatusEntry(ChangeKind.Added, parts[1], null));
                    break;

                case 'M' or 'T':
                    entries.Add(new NameStatusEntry(ChangeKind.Modified, parts[1], null));
                    break;

                case 'R' when parts.Length >= 3:
                    entries.Add(new NameStatusEntry(ChangeKind.Renamed, parts[2], parts[1]));
                    break;

                case 'C' when parts.Length >= 3:
                    entries.Add(new NameStatusEntry(ChangeKind.Added, parts[2], null));
                    break;

                case 'D':
                    break;

                default:
                    Log.Warning("Unrecognized --name-status line treated as modified: {Line}", rawLine);
                    entries.Add(new NameStatusEntry(ChangeKind.Modified, parts[^1], null));
                    break;
            }
        }

        return entries;
    }

    /// <summary>Extracts new-side line ranges from unified-diff hunk headers such as @@ -12,5 +12,7 @@; an omitted count means one line and a zero count marks a pure deletion with no new-side lines</summary>
    public static IReadOnlyList<LineRange> ParseHunkRanges(string diffOutput)
    {
        var ranges = new List<LineRange>();

        foreach (Match match in HunkHeaderRegex().Matches(diffOutput))
        {
            int start = int.Parse(match.Groups[1].Value);
            int count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;

            if (count > 0)
            {
                ranges.Add(new LineRange(start, start + count - 1));
            }
        }

        return ranges;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline)]
    private static partial Regex HunkHeaderRegex();
}
