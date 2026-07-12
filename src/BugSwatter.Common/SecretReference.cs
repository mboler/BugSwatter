namespace BugSwatter.Common;

/// <summary>Resolves a configured secret reference to its value. A secret is never a literal in the config: it is either
/// <c>env:VARIABLE_NAME</c>, read from the environment, or <c>file:PATH</c>, read from a file the operator restricts to
/// the account that runs the tool. The file form is the one to use for a Windows service or a cron job, because it avoids
/// machine-wide environment variables, which every process and user on the box can read</summary>
public static class SecretReference
{
    private const string EnvPrefix = "env:";
    private const string FilePrefix = "file:";

    /// <summary>True when the value is a well-formed env: or file: reference (prefix plus a non-empty target), not a literal or an empty reference</summary>
    public static bool IsReference(string? reference) => HasContent(reference, EnvPrefix) || HasContent(reference, FilePrefix);

    /// <summary>Resolves the reference to its secret value, or null when it is not a reference or the source cannot be read</summary>
    public static string? Resolve(string? reference) => Resolve(reference, null);

    /// <summary>Resolves the reference to its secret value, anchoring a relative file path to <paramref name="configDirectory"/></summary>
    public static string? Resolve(string? reference, string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (reference.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetEnvironmentVariable(reference[EnvPrefix.Length..].Trim());
        }

        if (reference.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string path = reference[FilePrefix.Length..].Trim();
            try
            {
                // Trim the trailing newline a secret file almost always carries; the file's own ACL is the protection
                if (path.Length == 0)
                {
                    return null;
                }

                string resolvedPath = configDirectory is null ? Path.GetFullPath(path) : ConfigLoader.ResolvePath(configDirectory, path);
                return File.ReadAllText(resolvedPath).Trim();
            }
            catch (Exception)
            {
                // catch-all: a missing or unreadable secret file resolves to null, so the caller skips the feature exactly as it would for an unset environment variable
                return null;
            }
        }

        return null;
    }

    private static bool HasContent(string? reference, string prefix) =>
        reference is not null && reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && reference[prefix.Length..].Trim().Length > 0;
}
