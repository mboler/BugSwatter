namespace Informant;

/// <summary>How a repository entry can participate in a review</summary>
public enum RepositoryManifestDisposition
{
    /// <summary>A bounded regular text file that may be reviewed</summary>
    Text,

    /// <summary>A tracked symbolic link that BugSwatter will never follow</summary>
    SymbolicLink,

    /// <summary>A Git submodule link whose external repository is outside this review boundary</summary>
    GitLink,

    /// <summary>A regular file containing binary data</summary>
    Binary,

    /// <summary>A regular file larger than the configured file limit</summary>
    TooLarge,

    /// <summary>A tracked file missing from the refreshed working tree</summary>
    Missing,

    /// <summary>A path containing a symbolic link, junction, mount point, or other reparse point</summary>
    ReparsePoint,

    /// <summary>A file that could not be inspected safely</summary>
    ReadFailed,

    /// <summary>A Git tree entry with an unsupported mode or object type</summary>
    UnsupportedGitEntry,

    /// <summary>A deleted file whose review content comes from the immutable baseline revision</summary>
    DeletedFromTip
}

/// <summary>Metadata for one current or deleted repository path in a review run</summary>
public sealed record RepositoryManifestEntry(string Path, string? GitMode, string? GitObjectType, string? GitObjectId, long? SizeBytes, int? LineCount, string? ContentHash, string Extension,
    bool RootLevel, RepositoryManifestDisposition Disposition, ChangeKind? ChangeKind = null, string? ContentRevision = null)
{
    /// <summary>Whether the entry has bounded text available to a review</summary>
    public bool Reviewable => Disposition is RepositoryManifestDisposition.Text or RepositoryManifestDisposition.DeletedFromTip;
}

/// <summary>A deterministic, tip-bound inventory rebuilt for one Informant run</summary>
public sealed record RepositoryManifest(string RepositoryUrl, string Branch, string WorkingTreeRoot, string? BaselineSha, string TipSha, ReviewMode ReviewMode, string RunStamp,
    DateTimeOffset GeneratedAt, IReadOnlyList<RepositoryManifestEntry> Entries)
{
    /// <summary>Number of paths represented in the run inventory</summary>
    public int EntryCount => Entries.Count;

    /// <summary>Number of entries with bounded text available to the review</summary>
    public int ReviewableCount => Entries.Count(entry => entry.Reviewable);

    /// <summary>Number of entries deliberately excluded from source review</summary>
    public int ExcludedCount => Entries.Count(entry => !entry.Reviewable);

    /// <summary>Number of entries selected by change detection or full-review mode</summary>
    public int SelectedCount => Entries.Count(entry => entry.ChangeKind is not null);

    /// <summary>Returns a copy annotated with the detected review set, including deleted-path tombstones</summary>
    public RepositoryManifest WithChanges(IReadOnlyList<ChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);

        Dictionary<string, RepositoryManifestEntry> entriesByPath = Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        foreach (ChangedFile changedFile in changedFiles)
        {
            if (entriesByPath.TryGetValue(changedFile.Path, out RepositoryManifestEntry? existing))
            {
                entriesByPath[changedFile.Path] = existing with { ChangeKind = changedFile.Kind, ContentRevision = changedFile.ContentRevision };
                continue;
            }

            RepositoryManifestDisposition disposition = changedFile.Kind == ChangeKind.Deleted
                ? RepositoryManifestDisposition.DeletedFromTip
                : RepositoryManifestDisposition.Missing;
            entriesByPath[changedFile.Path] = new RepositoryManifestEntry(changedFile.Path, null, null, null, null, null, null, Path.GetExtension(changedFile.Path),
                IsRootLevel(changedFile.Path), disposition, changedFile.Kind, changedFile.ContentRevision);
        }

        return this with { Entries = [.. entriesByPath.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal)] };
    }

    private static bool IsRootLevel(string path) => !path.Contains('/');
}
