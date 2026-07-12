namespace Informant.Tests;

/// <summary>Local git fixture: a bare origin plus a seed clone used to author commits, all inside one disposable temp directory</summary>
internal sealed class TestRepository : IDisposable
{
    private readonly GitRunner _git = new(TestGit.ExecutablePath);
    private readonly TempDirectory _root;

    private TestRepository(TempDirectory root)
    {
        _root = root;
        RemotePath = Path.Combine(root.Path, "origin.git");
        SeedPath = Path.Combine(root.Path, "seed");
    }

    /// <summary>Root temp directory holding the fixture; tests may place working trees under it</summary>
    public string Root => _root.Path;

    /// <summary>Path of the bare repository acting as the remote</summary>
    public string RemotePath { get; }

    /// <summary>Path of the clone used to author commits</summary>
    public string SeedPath { get; }

    /// <summary>Creates the bare origin and the seed clone</summary>
    public static async Task<TestRepository> CreateAsync()
    {
        var repository = new TestRepository(new TempDirectory());

        await repository._git.RunCheckedAsync("init", "--bare", "-b", "main", repository.RemotePath);
        await repository._git.RunCheckedAsync("clone", repository.RemotePath, repository.SeedPath);

        return repository;
    }

    /// <summary>Writes or overwrites a file, commits, pushes, and returns the new tip SHA</summary>
    public async Task<string> CommitFileAsync(string relativePath, string content, string message)
    {
        string fullPath = Path.Combine(SeedPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);

        await _git.RunCheckedAsync("-C", SeedPath, "add", relativePath);
        return await CommitAndPushAsync(message);
    }

    /// <summary>Deletes a file, commits, pushes, and returns the new tip SHA</summary>
    public async Task<string> DeleteFileAsync(string relativePath, string message)
    {
        await _git.RunCheckedAsync("-C", SeedPath, "rm", relativePath);
        return await CommitAndPushAsync(message);
    }

    /// <summary>Renames a file, commits, pushes, and returns the new tip SHA</summary>
    public async Task<string> RenameFileAsync(string fromPath, string toPath, string message)
    {
        await _git.RunCheckedAsync("-C", SeedPath, "mv", fromPath, toPath);
        return await CommitAndPushAsync(message);
    }

    /// <inheritdoc />
    public void Dispose() => _root.Dispose();

    private async Task<string> CommitAndPushAsync(string message)
    {
        await _git.RunCheckedAsync("-C", SeedPath, "-c", "user.name=Informant Tests", "-c", "user.email=tests@informant.local", "commit", "-m", message);
        await _git.RunCheckedAsync("-C", SeedPath, "push", "origin", "main");
        return (await _git.RunCheckedAsync("-C", SeedPath, "rev-parse", "HEAD")).Trim();
    }
}
