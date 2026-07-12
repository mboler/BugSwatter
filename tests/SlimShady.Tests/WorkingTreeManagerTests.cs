namespace SlimShady.Tests;

public sealed class WorkingTreeManagerTests
{
    [Fact]
    public async Task FirstUseClonesTreeAndDropsMarker()
    {
        using var repository = await TestRepository.CreateAsync();
        string expectedTip = await repository.CommitFileAsync("a.txt", "hello\n", "seed");
        string treePath = Path.Combine(repository.Root, "tree");
        WorkingTreeManager manager = CreateManager(repository, treePath);
        await manager.EnsureFreshTreeAsync();
        Assert.True(File.Exists(Path.Combine(treePath, WorkingTreeManager.MarkerFileName)));
        Assert.True(File.Exists(Path.Combine(treePath, "a.txt")));
        Assert.Equal(expectedTip, await manager.GetTipShaAsync());
    }

    [Fact]
    public async Task RefusesNonEmptyDirectoryWithoutMarker()
    {
        using var directory = new TempDirectory();
        string treePath = Path.Combine(directory.Path, "not-mine");
        Directory.CreateDirectory(treePath);
        File.WriteAllText(Path.Combine(treePath, "precious-work.txt"), "do not destroy");
        var manager = new WorkingTreeManager(new GitRunner(TestGit.ExecutablePath), "https://example.test/repo.git", "main", treePath);
        SlimShadyFatalException ex = await Assert.ThrowsAsync<SlimShadyFatalException>(manager.EnsureFreshTreeAsync);
        Assert.Contains(WorkingTreeManager.MarkerFileName, ex.Message);
        Assert.True(File.Exists(Path.Combine(treePath, "precious-work.txt")));
    }

    [Fact]
    public async Task RefreshRestoresPristineMirrorAndKeepsMarker()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("a.txt", "original\n", "seed");
        string treePath = Path.Combine(repository.Root, "tree");
        WorkingTreeManager manager = CreateManager(repository, treePath);
        await manager.EnsureFreshTreeAsync();

        // Dirty the tree the way an accident would: edit a tracked file, add untracked junk
        File.WriteAllText(Path.Combine(treePath, "a.txt"), "local tampering");
        File.WriteAllText(Path.Combine(treePath, "junk.txt"), "junk");
        Directory.CreateDirectory(Path.Combine(treePath, "junkdir"));
        File.WriteAllText(Path.Combine(treePath, "junkdir", "more-junk.txt"), "junk");

        string newTip = await repository.CommitFileAsync("b.txt", "new file\n", "second commit");
        await manager.EnsureFreshTreeAsync();

        Assert.Equal(newTip, await manager.GetTipShaAsync());
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(treePath, "a.txt")).Replace("\r\n", "\n"));
        Assert.True(File.Exists(Path.Combine(treePath, "b.txt")));
        Assert.False(File.Exists(Path.Combine(treePath, "junk.txt")));
        Assert.False(Directory.Exists(Path.Combine(treePath, "junkdir")));
        Assert.True(File.Exists(Path.Combine(treePath, WorkingTreeManager.MarkerFileName)));
    }

    [Fact]
    public async Task RefusesPreviouslyOwnedTreeWhoseMarkerVanished()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("a.txt", "original\n", "seed");
        string treePath = Path.Combine(repository.Root, "tree");
        WorkingTreeManager manager = CreateManager(repository, treePath);
        await manager.EnsureFreshTreeAsync();

        // Simulate an operator or another process removing the claim, then tamper a tracked file
        File.Delete(Path.Combine(treePath, WorkingTreeManager.MarkerFileName));
        File.WriteAllText(Path.Combine(treePath, "a.txt"), "must survive");

        await Assert.ThrowsAsync<SlimShadyFatalException>(manager.EnsureFreshTreeAsync);

        // The refusal must have fired before any destructive git command ran
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(treePath, "a.txt")));
    }

    private static WorkingTreeManager CreateManager(TestRepository repository, string treePath) => new(new GitRunner(TestGit.ExecutablePath), repository.RemotePath, "main", treePath);
}
