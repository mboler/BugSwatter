using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Safely prepared source lines or an immediate deterministic file disposition</summary>
public sealed record PreparedReviewFile(ChangedFile File, string[]? Lines, FileReviewResult? ImmediateResult);

/// <summary>Loads current or deleted repository text once through the shared bounded and symbolic-link-safe review rules</summary>
public sealed class RepositoryReviewSourceLoader
{
    private readonly RepositoryFileReader _fileReader;
    private readonly GitRunner? _git;
    private readonly string _treeRoot;
    private readonly int _maxFileBytes;

    /// <summary>Creates a source loader for one refreshed working tree</summary>
    public RepositoryReviewSourceLoader(string treeRoot, int maxFileBytes = RepositoryFileReader.DefaultMaxFileBytes, GitRunner? git = null)
    {
        _fileReader = new RepositoryFileReader(treeRoot, maxFileBytes);
        _git = git;
        _treeRoot = treeRoot;
        _maxFileBytes = maxFileBytes;
    }

    /// <summary>Returns bounded text lines or an immediate not-reviewable or repository-failure result</summary>
    public async Task<PreparedReviewFile> LoadAsync(ChangedFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Kind is ChangeKind.Modified or ChangeKind.Renamed && file.ChangedRanges.Count == 0)
        {
            return Immediate(file, NotReviewable(file, "no line changes (pure rename or metadata-only change)"));
        }

        string[] lines;
        try
        {
            lines = file.Kind == ChangeKind.Deleted
                ? await ReadDeletedLinesAsync(file)
                : await _fileReader.ReadAllLinesAsync(file.Path, cancellationToken);
        }
        catch (RepositoryFileException ex)
        {
            string reason = ex.Error == RepositoryFileError.NotFound ? "file not present in the working tree" : ex.Message;
            FileReviewResult result = IsExpectedExclusion(ex.Error) ? NotReviewable(file, reason) : Failed(file, reason);
            return Immediate(file, result);
        }

        if (lines.Length == 0)
        {
            return Immediate(file, NotReviewable(file, "empty file"));
        }

        if (Array.Exists(lines, line => line.Contains('\0')))
        {
            return Immediate(file, NotReviewable(file, "binary file"));
        }

        return new PreparedReviewFile(file, lines, null);
    }

    private static PreparedReviewFile Immediate(ChangedFile file, FileReviewResult result) => new(file, null, result);

    private static FileReviewResult NotReviewable(ChangedFile file, string reason)
    {
        Log.Information("Not reviewing {Path}: {Reason}", file.Path, reason);
        return new FileReviewResult(file, FileReviewStatus.NotReviewable, null, 0, 0, reason);
    }

    private static FileReviewResult Failed(ChangedFile file, string reason)
    {
        Log.Warning("Review failed for {Path}: {Reason}", file.Path, reason);
        return new FileReviewResult(file, FileReviewStatus.Failed, null, 0, 0, reason, FailureKind: FileReviewFailureKind.Repository);
    }

    private static bool IsExpectedExclusion(RepositoryFileError error) => error is RepositoryFileError.ReparsePoint or RepositoryFileError.TooLarge or RepositoryFileError.Binary;

    private async Task<string[]> ReadDeletedLinesAsync(ChangedFile file)
    {
        if (_git is null || string.IsNullOrWhiteSpace(file.ContentRevision))
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"deleted file '{file.Path}' has no baseline Git revision available");
        }

        return await GitBlobReader.ReadLinesAsync(_git, _treeRoot, file.ContentRevision, file.Path, _maxFileBytes);
    }
}
