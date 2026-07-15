using System.Globalization;
using System.Text;
using BugSwatter.Common;

namespace BugSwatter.Git;

/// <summary>Reads bounded text from immutable Git blob objects</summary>
public static class GitBlobReader
{
    /// <summary>Reads every line from a bounded text blob at the requested revision and path</summary>
    public static async Task<string[]> ReadLinesAsync(GitRunner git, string treePath, string revision, string path, int maxFileBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(treePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);

        string objectName = $"{revision}:{path}";
        GitResult sizeResult = await git.RunAsync(cancellationToken, "-C", treePath, "cat-file", "-s", objectName).ConfigureAwait(false);
        if (sizeResult.ExitCode != 0 || !long.TryParse(sizeResult.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size))
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"Git could not inspect '{path}' at baseline {revision} (cat-file -s exited {sizeResult.ExitCode})");
        }

        if (size > maxFileBytes)
        {
            throw new RepositoryFileException(RepositoryFileError.TooLarge, $"'{path}' exceeds maxFileBytes limit of {maxFileBytes} bytes in baseline {revision}");
        }

        GitResult contentResult = await git.RunAsync(cancellationToken, "-C", treePath, "cat-file", "blob", objectName).ConfigureAwait(false);
        if (contentResult.ExitCode != 0)
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"Git could not read '{path}' at baseline {revision} (cat-file blob exited {contentResult.ExitCode})");
        }

        if (Encoding.UTF8.GetByteCount(contentResult.StandardOutput) > maxFileBytes)
        {
            throw new RepositoryFileException(RepositoryFileError.TooLarge, $"'{path}' grew beyond maxFileBytes limit of {maxFileBytes} bytes while reading baseline {revision}");
        }

        if (contentResult.StandardOutput.Contains('\0'))
        {
            throw new RepositoryFileException(RepositoryFileError.Binary, $"'{path}' is a binary file in baseline {revision}");
        }

        var lines = new List<string>();
        using var reader = new StringReader(contentResult.StandardOutput);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }
}
