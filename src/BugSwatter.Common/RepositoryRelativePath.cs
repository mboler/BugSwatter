namespace BugSwatter.Common;

/// <summary>Normalizes repository-relative manifest paths without consulting or following the live filesystem</summary>
public static class RepositoryRelativePath
{
    /// <summary>Returns a canonical forward-slash path or throws when the value is absolute, empty, or contains parent traversal</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Contains('\0') || Path.IsPathFullyQualified(path) || path.StartsWith('/') || path.StartsWith('\\') || IsWindowsDrivePath(path))
        {
            throw new ArgumentException($"Path must be relative to the repository root: '{path}'", nameof(path));
        }

        string[] components = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(components.Length);
        foreach (string component in components)
        {
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                throw new ArgumentException($"Path must not contain parent traversal: '{path}'", nameof(path));
            }

            if (component.EndsWith(' ') || component.EndsWith('.'))
            {
                throw new ArgumentException($"Path contains a Windows-ambiguous trailing dot or space: '{path}'", nameof(path));
            }

            normalized.Add(component);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException("Path must identify repository content", nameof(path));
        }

        return string.Join('/', normalized);
    }

    /// <summary>Attempts to return a canonical forward-slash path without throwing for model-supplied input</summary>
    public static bool TryNormalize(string? path, out string normalized)
    {
        try
        {
            normalized = Normalize(path ?? "");
            return true;
        }
        catch (ArgumentException)
        {
            normalized = "";
            return false;
        }
    }

    private static bool IsWindowsDrivePath(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}
