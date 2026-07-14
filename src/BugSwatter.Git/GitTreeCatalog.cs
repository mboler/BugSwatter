namespace BugSwatter.Git;

/// <summary>How a tracked Git tree entry is represented</summary>
public enum GitTreeEntryKind
{
    /// <summary>A regular tracked file</summary>
    RegularFile,

    /// <summary>A tracked symbolic link</summary>
    SymbolicLink,

    /// <summary>A tracked Git submodule link</summary>
    GitLink,

    /// <summary>An entry type or mode BugSwatter does not understand</summary>
    Unsupported
}

/// <summary>One immutable entry read from a Git tree object</summary>
public sealed record GitTreeEntry(string Path, string Mode, string ObjectType, string ObjectId, GitTreeEntryKind Kind);

/// <summary>Lists and parses tracked entries directly from an immutable Git tree</summary>
public sealed class GitTreeCatalog
{
    private readonly GitRunner _git;
    private readonly string _treePath;

    /// <summary>Creates a catalog for the supplied working tree</summary>
    public GitTreeCatalog(GitRunner git, string treePath)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(treePath);

        _git = git;
        _treePath = treePath;
    }

    /// <summary>Lists every tracked leaf entry from the requested revision without consulting the mutable Git index</summary>
    public async Task<IReadOnlyList<GitTreeEntry>> ListAsync(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        string output = await _git.RunCheckedAsync("-C", _treePath, "ls-tree", "-rz", "--full-tree", revision).ConfigureAwait(false);
        return Parse(output);
    }

    /// <summary>Parses null-delimited git ls-tree output while preserving every legal filename character</summary>
    public static IReadOnlyList<GitTreeEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var entries = new List<GitTreeEntry>();
        foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int pathSeparator = record.IndexOf('\t');
            if (pathSeparator <= 0 || pathSeparator == record.Length - 1)
            {
                throw new GitOperationException("git ls-tree returned an entry without the expected metadata and path fields");
            }

            string[] metadata = record[..pathSeparator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3)
            {
                throw new GitOperationException("git ls-tree returned an entry without mode, type, and object ID fields");
            }

            string mode = metadata[0];
            string objectType = metadata[1];
            string objectId = metadata[2];
            string path = record[(pathSeparator + 1)..];
            entries.Add(new GitTreeEntry(path, mode, objectType, objectId, Classify(mode, objectType)));
        }

        return [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)];
    }

    private static GitTreeEntryKind Classify(string mode, string objectType) => (mode, objectType) switch
    {
        ("120000", "blob") => GitTreeEntryKind.SymbolicLink,
        ("160000", "commit") => GitTreeEntryKind.GitLink,
        (_, "blob") when mode.StartsWith("100", StringComparison.Ordinal) => GitTreeEntryKind.RegularFile,
        _ => GitTreeEntryKind.Unsupported
    };
}
