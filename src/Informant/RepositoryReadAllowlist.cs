using BugSwatter.Common;

namespace Informant;

/// <summary>Result of validating one requested path against the run manifest and live read boundary</summary>
public sealed record RepositoryReadAuthorization(bool Allowed, string? NormalizedPath, string? ReadPath, RepositoryManifestEntry? Entry, string? ReasonCode, string? Message);

/// <summary>Authorizes live repository reads only when the resolved path is a reviewable text entry in the current run manifest</summary>
public sealed class RepositoryReadAllowlist
{
    private readonly RepositoryPathResolver _readRootResolver;
    private readonly string _manifestRoot;
    private readonly IReadOnlyDictionary<string, RepositoryManifestEntry> _entries;

    /// <summary>Creates an allowlist for one manifest and configured read root</summary>
    public RepositoryReadAllowlist(RepositoryManifest manifest, string readRoot)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(readRoot);

        _readRootResolver = new RepositoryPathResolver(readRoot);
        _manifestRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.WorkingTreeRoot));
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _entries = manifest.Entries.ToDictionary(entry => entry.Path, comparer);
    }

    /// <summary>Resolves and validates a requested path without following a symbolic link or accepting an unmanifested file</summary>
    public RepositoryReadAuthorization Authorize(string requestedPath)
    {
        string fullPath;
        try
        {
            fullPath = _readRootResolver.ResolveFile(requestedPath);
        }
        catch (RepositoryFileException ex)
        {
            return new RepositoryReadAuthorization(false, null, null, null, ex.Error.ToString(), ex.Message);
        }

        string manifestRelativePath = Path.GetRelativePath(_manifestRoot, fullPath);
        if (IsOutsideRoot(manifestRelativePath))
        {
            return new RepositoryReadAuthorization(false, null, null, null, "OutsideManifestRoot", "resolved path is outside the repository represented by the current manifest");
        }

        string normalizedPath = manifestRelativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        if (!_entries.TryGetValue(normalizedPath, out RepositoryManifestEntry? entry))
        {
            return new RepositoryReadAuthorization(false, normalizedPath, null, null, "UnknownPath", "path is not present in the current repository manifest");
        }

        if (entry.Disposition != RepositoryManifestDisposition.Text || entry.SizeBytes is null || entry.LineCount is null || entry.ContentHash is null)
        {
            return new RepositoryReadAuthorization(false, normalizedPath, null, entry, entry.Disposition.ToString(), $"manifest entry is not reviewable text: {entry.Disposition}");
        }

        string readPath = Path.GetRelativePath(_readRootResolver.Root, fullPath);
        return new RepositoryReadAuthorization(true, normalizedPath, readPath, entry, null, null);
    }

    /// <summary>Returns true only when a completed live read still matches the exact metadata captured in the manifest</summary>
    public static bool MatchesSnapshot(RepositoryManifestEntry entry, RepositoryLineRange result) => entry.SizeBytes == result.SizeBytes && entry.LineCount == result.TotalLines
        && string.Equals(entry.ContentHash, result.ContentHash, StringComparison.Ordinal);

    private static bool IsOutsideRoot(string relativePath) => Path.IsPathFullyQualified(relativePath) || relativePath == ".."
        || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
}
