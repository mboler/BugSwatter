using BugSwatter.Common;

namespace Informant;

/// <summary>Builds an Informant repository manifest from an immutable Git tree and the safely inspected working files</summary>
public sealed class RepositoryManifestBuilder
{
    private readonly GitTreeCatalog _catalog;
    private readonly RepositoryFileReader _fileReader;

    /// <summary>Creates a builder for one refreshed working tree</summary>
    public RepositoryManifestBuilder(GitTreeCatalog catalog, string treePath, int maxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _catalog = catalog;
        _fileReader = new RepositoryFileReader(treePath, maxFileBytes);
    }

    /// <summary>Rebuilds the complete manifest for the supplied immutable tip revision</summary>
    public async Task<RepositoryManifest> BuildAsync(string repositoryUrl, string branch, string? baselineSha, string tipSha, ReviewMode reviewMode, string runStamp, DateTimeOffset generatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(tipSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(runStamp);

        IReadOnlyList<GitTreeEntry> trackedEntries = await _catalog.ListAsync(tipSha);
        var entries = new List<RepositoryManifestEntry>(trackedEntries.Count);
        foreach (GitTreeEntry trackedEntry in trackedEntries)
        {
            entries.Add(Inspect(trackedEntry));
        }

        return new RepositoryManifest(repositoryUrl, branch, _fileReader.Root, baselineSha, tipSha, reviewMode, runStamp, generatedAt, entries);
    }

    private RepositoryManifestEntry Inspect(GitTreeEntry entry)
    {
        RepositoryManifestDisposition disposition;
        long? sizeBytes = null;
        int? lineCount = null;
        string? contentHash = null;

        switch (entry.Kind)
        {
            case GitTreeEntryKind.SymbolicLink:
                disposition = RepositoryManifestDisposition.SymbolicLink;
                break;

            case GitTreeEntryKind.GitLink:
                disposition = RepositoryManifestDisposition.GitLink;
                break;

            case GitTreeEntryKind.Unsupported:
                disposition = RepositoryManifestDisposition.UnsupportedGitEntry;
                break;

            case GitTreeEntryKind.RegularFile:
                try
                {
                    RepositoryFileInspection inspection = _fileReader.Inspect(entry.Path);
                    sizeBytes = inspection.SizeBytes;
                    lineCount = inspection.LineCount;
                    contentHash = inspection.ContentHash;
                    disposition = RepositoryManifestDisposition.Text;
                }
                catch (RepositoryFileException ex)
                {
                    disposition = Map(ex.Error);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(entry), entry.Kind, "Unknown Git tree entry kind");
        }

        return new RepositoryManifestEntry(entry.Path, entry.Mode, entry.ObjectType, entry.ObjectId, sizeBytes, lineCount, contentHash, Path.GetExtension(entry.Path), IsRootLevel(entry.Path), disposition);
    }

    private static RepositoryManifestDisposition Map(RepositoryFileError error) => error switch
    {
        RepositoryFileError.Binary => RepositoryManifestDisposition.Binary,
        RepositoryFileError.TooLarge => RepositoryManifestDisposition.TooLarge,
        RepositoryFileError.NotFound => RepositoryManifestDisposition.Missing,
        RepositoryFileError.ReparsePoint => RepositoryManifestDisposition.ReparsePoint,
        RepositoryFileError.InvalidPath or RepositoryFileError.OutsideRoot or RepositoryFileError.ReadFailed => RepositoryManifestDisposition.ReadFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown repository file error")
    };

    private static bool IsRootLevel(string path) => !path.Contains('/');
}
