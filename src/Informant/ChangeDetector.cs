using System.Text.RegularExpressions;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>One parsed null-delimited git diff --name-status entry</summary>
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
        // --verify --quiet gives a locale-independent exit code: 1 means the object is absent, while operational failures use another code
        // The doubled braces are C# escapes: this renders as <sha>^{commit}, Git's peel-to-commit revision syntax
        var result = await _git.RunAsync("-C", _treePath, "rev-parse", "--verify", "--quiet", $"{sha}^{{commit}}");
        if (result.ExitCode == 0)
        {
            return true;
        }

        if (result.ExitCode == 1)
        {
            return false;
        }

        throw new GitOperationException($"Could not verify baseline commit {sha}: git rev-parse exited {result.ExitCode}: {TextSummary.Create(result.StandardError, 500)}");
    }

    /// <summary>Lists reviewable files changed between the two commits, each with its changed line ranges on the new side</summary>
    public async Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baselineSha, string tipSha)
    {
        string nameStatus = await _git.RunCheckedAsync("-C", _treePath, "diff", "--name-status", "-z", baselineSha, tipSha);

        var files = new List<ChangedFile>();
        foreach (var entry in ParseNameStatus(nameStatus))
        {
            if (entry.Kind == ChangeKind.Deleted)
            {
                files.Add(new ChangedFile(entry.Path, ChangeKind.Deleted, [], baselineSha));
                continue;
            }

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
        IReadOnlyList<GitTreeEntry> entries = await new GitTreeCatalog(_git, _treePath).ListAsync("HEAD");
        return [.. entries.Select(entry => new ChangedFile(entry.Path, ChangeKind.FullReview, []))];
    }

    /// <summary>Parses null-delimited git diff --name-status output without trimming any legal filename character</summary>
    public static IReadOnlyList<NameStatusEntry> ParseNameStatus(string output)
    {
        var entries = new List<NameStatusEntry>();
        string[] fields = output.Split('\0');
        int index = 0;
        while (index < fields.Length && fields[index].Length > 0)
        {
            string status = fields[index++];
            if (index >= fields.Length || fields[index].Length == 0)
            {
                Log.Warning("Incomplete null-delimited --name-status entry for status {Status}", status);
                break;
            }

            string firstPath = fields[index++];
            switch (status[0])
            {
                case 'A':
                    entries.Add(new NameStatusEntry(ChangeKind.Added, firstPath, null));
                    break;

                case 'M' or 'T':
                    entries.Add(new NameStatusEntry(ChangeKind.Modified, firstPath, null));
                    break;

                case 'R' when TryReadSecondPath(fields, ref index, status, out string renamedPath):
                    entries.Add(new NameStatusEntry(ChangeKind.Renamed, renamedPath, firstPath));
                    break;

                case 'C' when TryReadSecondPath(fields, ref index, status, out string copiedPath):
                    entries.Add(new NameStatusEntry(ChangeKind.Added, copiedPath, null));
                    break;

                case 'D':
                    entries.Add(new NameStatusEntry(ChangeKind.Deleted, firstPath, null));
                    break;

                default:
                    Log.Warning("Unrecognized --name-status status treated as modified: {Status}", status);
                    entries.Add(new NameStatusEntry(ChangeKind.Modified, firstPath, null));
                    break;
            }
        }

        return entries;
    }

    private static bool TryReadSecondPath(string[] fields, ref int index, string status, out string path)
    {
        if (index < fields.Length && fields[index].Length > 0)
        {
            path = fields[index++];
            return true;
        }

        Log.Warning("Incomplete null-delimited --name-status entry for status {Status}", status);
        path = "";
        return false;
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
