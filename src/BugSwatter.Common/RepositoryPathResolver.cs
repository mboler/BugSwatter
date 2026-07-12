namespace BugSwatter.Common;

/// <summary>Why a repository file could not be safely resolved or read</summary>
public enum RepositoryFileError
{
    /// <summary>The requested path was malformed or absolute</summary>
    InvalidPath,

    /// <summary>The requested path resolved outside the repository root</summary>
    OutsideRoot,

    /// <summary>The requested file does not exist</summary>
    NotFound,

    /// <summary>The path contains a symbolic link, junction, mount point, or other reparse point</summary>
    ReparsePoint,

    /// <summary>The file exceeded the configured byte limit</summary>
    TooLarge,

    /// <summary>The file contains binary data</summary>
    Binary,

    /// <summary>The file could not be opened or read</summary>
    ReadFailed
}

/// <summary>A safe repository-file failure carrying a stable category for reports and tool results</summary>
public sealed class RepositoryFileException : Exception
{
    /// <summary>Creates a categorized repository-file failure</summary>
    public RepositoryFileException(RepositoryFileError error, string message, Exception? innerException = null) : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>Stable failure category</summary>
    public RepositoryFileError Error { get; }
}

/// <summary>Resolves relative repository paths and rejects every escape and reparse-point component</summary>
public sealed class RepositoryPathResolver
{
    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly StringComparison _comparison;

    /// <summary>Creates a resolver confined to <paramref name="root"/></summary>
    public RepositoryPathResolver(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootWithSeparator = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        RejectReparsePoint(_root, ".");
    }

    /// <summary>Absolute repository root</summary>
    public string Root => _root;

    /// <summary>Resolves an existing regular file below the root and rejects reparse points in every traversed component</summary>
    public string ResolveFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new RepositoryFileException(RepositoryFileError.InvalidPath, "path is required");
        }

        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new RepositoryFileException(RepositoryFileError.InvalidPath, $"path '{relativePath}' must be relative to the repository root");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(relativePath, _root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new RepositoryFileException(RepositoryFileError.InvalidPath, $"invalid path '{relativePath}': {ex.Message}", ex);
        }

        if (!fullPath.StartsWith(_rootWithSeparator, _comparison))
        {
            throw new RepositoryFileException(RepositoryFileError.OutsideRoot, $"path '{relativePath}' resolves outside the allowed read root");
        }

        string current = _root;
        string repositoryRelativePath = Path.GetRelativePath(_root, fullPath);
        foreach (string component in repositoryRelativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            RejectReparsePoint(current, relativePath);
        }

        if (!File.Exists(fullPath))
        {
            throw new RepositoryFileException(RepositoryFileError.NotFound, $"file not found: {relativePath}");
        }

        return fullPath;
    }

    private static void RejectReparsePoint(string path, string displayPath)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new RepositoryFileException(RepositoryFileError.ReparsePoint, $"path '{displayPath}' contains a symbolic link, junction, mount point, or other reparse point");
            }
        }
        catch (RepositoryFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RepositoryFileException(RepositoryFileError.NotFound, $"file not found: {displayPath}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"could not inspect '{displayPath}': {ex.Message}", ex);
        }
    }
}
