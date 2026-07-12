using System.Text;
using System.Text.Json;
using Serilog;

namespace BugSwatter.Git;

/// <summary>Versioned ownership record stored both inside and outside a managed Git working tree</summary>
public sealed record WorkingTreeOwnership
{
    /// <summary>Ownership record format version</summary>
    public int Version { get; init; }

    /// <summary>Random identity tying the internal marker to its external claim</summary>
    public Guid ClaimId { get; init; }

    /// <summary>Canonical absolute path claimed by the record</summary>
    public string CanonicalPath { get; init; } = "";

    /// <summary>Configured repository remote</summary>
    public string RepositoryUrl { get; init; } = "";

    /// <summary>Configured branch</summary>
    public string Branch { get; init; } = "";

    /// <summary>UTC time the tree was first claimed</summary>
    public DateTimeOffset ClaimedAtUtc { get; init; }
}

/// <summary>Owns a Git working tree and validates paired ownership records before every destructive refresh</summary>
public sealed class WorkingTreeManager
{
    /// <summary>Name of the versioned ownership marker at the tree root</summary>
    public const string MarkerFileName = ".bugswatter";

    /// <summary>Current ownership record format</summary>
    public const int OwnershipVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly GitRunner _git;
    private readonly string _repositoryUrl;
    private readonly string _branch;
    private readonly string _treePath;
    private readonly string _canonicalTreePath;
    private readonly string _claimFilePath;
    private readonly StringComparison _pathComparison;

    /// <summary>Creates a manager for the configured tree</summary>
    public WorkingTreeManager(GitRunner git, string repositoryUrl, string branch, string treePath)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
        _repositoryUrl = repositoryUrl;
        _branch = branch;
        _treePath = treePath;
        _canonicalTreePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(treePath));
        _claimFilePath = GetClaimFilePath(_canonicalTreePath);
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>Returns the external ownership-claim path paired with a working tree</summary>
    public static string GetClaimFilePath(string treePath) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(treePath)) + ".bugswatter-claim.json";

    /// <summary>Ensures the tree exists and matches the remote branch exactly: clones and claims on first use, otherwise validates ownership and refreshes destructively</summary>
    public async Task EnsureFreshTreeAsync()
    {
        if (Directory.Exists(_treePath) && (File.GetAttributes(_treePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GitOperationException($"Refusing to use workingTreePath {_treePath}: the directory is a symbolic link, junction, mount point, or other reparse point");
        }

        if (!Directory.Exists(_treePath) || !Directory.EnumerateFileSystemEntries(_treePath).Any())
        {
            await InitializeAsync();
            return;
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

        if (File.Exists(_claimFilePath) || Directory.Exists(_claimFilePath))
        {
            throw new GitOperationException($"Refusing to initialize {_treePath}: external ownership claim already exists at {_claimFilePath}");
        }

        await _git.RunCheckedAsync("clone", "--branch", _branch, _repositoryUrl, _treePath);

        var ownership = new WorkingTreeOwnership
        {
            Version = OwnershipVersion,
            ClaimId = Guid.NewGuid(),
            CanonicalPath = _canonicalTreePath,
            RepositoryUrl = _repositoryUrl,
            Branch = _branch,
            ClaimedAtUtc = DateTimeOffset.UtcNow
        };
        string json = JsonSerializer.Serialize(ownership, JsonOptions) + Environment.NewLine;

        await WriteNewFileAsync(_claimFilePath, json);
        await WriteNewFileAsync(Path.Combine(_treePath, MarkerFileName), json);

        Log.Information("Working tree initialized and claimed with ID {ClaimId}", ownership.ClaimId);
    }

    private async Task RefreshAsync()
    {
        await ValidateOwnershipAsync();

        Log.Information("Refreshing working tree {Tree} to a pristine mirror of origin/{Branch}", _treePath, _branch);

        await _git.RunCheckedAsync("-C", _treePath, "fetch", "origin", _branch);
        await _git.RunCheckedAsync("-C", _treePath, "reset", "--hard", $"origin/{_branch}");

        // -e keeps the ownership marker; -x also removes ignored files so the mirror stays pristine.
        await _git.RunCheckedAsync("-C", _treePath, "clean", "-fdx", "-e", MarkerFileName);
    }

    private async Task ValidateOwnershipAsync()
    {
        string markerPath = Path.Combine(_treePath, MarkerFileName);
        WorkingTreeOwnership marker = await ReadOwnershipAsync(markerPath, "working-tree marker");
        WorkingTreeOwnership claim = await ReadOwnershipAsync(_claimFilePath, "external claim");

        ValidateRecord(marker, "working-tree marker");
        ValidateRecord(claim, "external claim");
        if (marker != claim)
        {
            throw Refusal("the working-tree marker does not match its external ownership claim");
        }

        string gitDirectory = Path.Combine(_treePath, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            throw Refusal("the .git directory is missing");
        }

        if ((File.GetAttributes(gitDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refusal("the .git directory is a symbolic link or reparse point");
        }

        GitResult originResult = await _git.RunAsync("-C", _treePath, "remote", "get-url", "origin");
        string origin = originResult.StandardOutput.TrimEnd('\r', '\n');
        if (originResult.ExitCode != 0 || !string.Equals(origin, _repositoryUrl, StringComparison.Ordinal))
        {
            throw Refusal($"origin remote is '{origin}', expected '{_repositoryUrl}'");
        }

        GitResult branchResult = await _git.RunAsync("-C", _treePath, "branch", "--show-current");
        string branch = branchResult.StandardOutput.TrimEnd('\r', '\n');
        if (branchResult.ExitCode != 0 || !string.Equals(branch, _branch, StringComparison.Ordinal))
        {
            throw Refusal($"checked-out branch is '{branch}', expected '{_branch}'");
        }
    }

    private void ValidateRecord(WorkingTreeOwnership ownership, string source)
    {
        if (ownership.Version != OwnershipVersion)
        {
            throw Refusal($"the {source} has unsupported version {ownership.Version}");
        }

        if (ownership.ClaimId == Guid.Empty)
        {
            throw Refusal($"the {source} has an empty claim ID");
        }

        if (!string.Equals(ownership.CanonicalPath, _canonicalTreePath, _pathComparison))
        {
            throw Refusal($"the {source} claims path '{ownership.CanonicalPath}', expected '{_canonicalTreePath}'");
        }

        if (!string.Equals(ownership.RepositoryUrl, _repositoryUrl, StringComparison.Ordinal))
        {
            throw Refusal($"the {source} claims repository '{ownership.RepositoryUrl}', expected '{_repositoryUrl}'");
        }

        if (!string.Equals(ownership.Branch, _branch, StringComparison.Ordinal))
        {
            throw Refusal($"the {source} claims branch '{ownership.Branch}', expected '{_branch}'");
        }

        if (ownership.ClaimedAtUtc == default)
        {
            throw Refusal($"the {source} has no claim timestamp");
        }
    }

    private static async Task<WorkingTreeOwnership> ReadOwnershipAsync(string path, string source)
    {
        if (!File.Exists(path))
        {
            throw new GitOperationException($"Refusing to run destructive git operations: the {source} is missing at {path}");
        }

        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new GitOperationException($"Refusing to run destructive git operations: the {source} is a symbolic link or reparse point at {path}");
            }

            string json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<WorkingTreeOwnership>(json, JsonOptions)
                ?? throw new JsonException("the ownership document was empty");
        }
        catch (GitOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new GitOperationException($"Refusing to run destructive git operations: the {source} at {path} is invalid: {ex.Message}", ex);
        }
    }

    private GitOperationException Refusal(string reason) => new($"Refusing to run destructive git operations in {_treePath}: {reason}");

    private static async Task WriteNewFileAsync(string path, string content)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GitOperationException($"Could not create ownership record {path}: {ex.Message}", ex);
        }
    }
}
