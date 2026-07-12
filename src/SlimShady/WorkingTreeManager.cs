using Serilog;

namespace SlimShady;

/// <summary>Owns SlimShady's working tree: first use clones the remote branch and claims the directory with a marker file, every later run verifies the marker and refreshes the tree destructively to a pristine mirror of the remote</summary>
public sealed class WorkingTreeManager
{
    /// <summary>Name of the ownership marker file at the tree root; destructive operations refuse to run when it is absent</summary>
    public const string MarkerFileName = ".slimshady";

    private readonly GitRunner _git;
    private readonly string _repositoryUrl;
    private readonly string _branch;
    private readonly string _treePath;

    /// <summary>Creates a manager for the configured tree</summary>
    public WorkingTreeManager(GitRunner git, string repositoryUrl, string branch, string treePath)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
        _repositoryUrl = repositoryUrl;
        _branch = branch;
        _treePath = treePath;
    }

    /// <summary>Ensures the tree exists and matches the remote branch exactly: clones and claims on first use, otherwise verifies ownership and refreshes destructively</summary>
    public async Task EnsureFreshTreeAsync()
    {
        if (!Directory.Exists(_treePath) || !Directory.EnumerateFileSystemEntries(_treePath).Any())
        {
            await InitializeAsync();
            return;
        }

        if (!File.Exists(Path.Combine(_treePath, MarkerFileName)))
        {
            throw new SlimShadyFatalException($"Refusing to touch {_treePath}: the directory is not empty and has no {MarkerFileName} marker. SlimShady only runs destructive git operations on a tree it created and claimed itself. Point workingTreePath at a directory dedicated to SlimShady");
        }

        await RefreshAsync();
    }

    /// <summary>Reads the SHA of the current branch tip</summary>
    public async Task<string> GetTipShaAsync() => (await _git.RunCheckedAsync("-C", _treePath, "rev-parse", "HEAD")).Trim();

    private async Task InitializeAsync()
    {
        Log.Information("Initializing working tree: cloning {Repository} branch {Branch} into {Tree}", _repositoryUrl, _branch, _treePath);

        string? parent = Path.GetDirectoryName(_treePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await _git.RunCheckedAsync("clone", "--branch", _branch, _repositoryUrl, _treePath);

        string markerText =
            $"This directory is owned by SlimShady and is overwritten destructively on every run.{Environment.NewLine}Repository: {_repositoryUrl}{Environment.NewLine}Branch: {_branch}{Environment.NewLine}Claimed: {DateTimeOffset.Now:O}{Environment.NewLine}";

        await File.WriteAllTextAsync(Path.Combine(_treePath, MarkerFileName), markerText);

        Log.Information("Working tree initialized and claimed with {Marker}", MarkerFileName);
    }

    private async Task RefreshAsync()
    {
        // Structural backstop: this method owns the only destructive git commands, so it verifies ownership itself.
        // EnsureFreshTreeAsync performs the same check earlier for the richer user-facing message; this one makes the
        // guarantee hold even if a future change ever calls RefreshAsync through another path
        if (!File.Exists(Path.Combine(_treePath, MarkerFileName)))
        {
            throw new SlimShadyFatalException($"Refusing to run destructive git operations in {_treePath}: the {MarkerFileName} ownership marker is missing");
        }

        Log.Information("Refreshing working tree {Tree} to a pristine mirror of origin/{Branch}", _treePath, _branch);

        await _git.RunCheckedAsync("-C", _treePath, "fetch", "origin", _branch);
        await _git.RunCheckedAsync("-C", _treePath, "reset", "--hard", $"origin/{_branch}");

        // -e keeps the ownership marker; -x also removes ignored files so the mirror stays pristine
        await _git.RunCheckedAsync("-C", _treePath, "clean", "-fdx", "-e", MarkerFileName);
    }
}
