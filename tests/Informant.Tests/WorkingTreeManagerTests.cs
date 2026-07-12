using System.Text.Json;
using System.Text.Json.Nodes;

namespace Informant.Tests;

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
        string markerPath = Path.Combine(treePath, WorkingTreeManager.MarkerFileName);
        string claimPath = WorkingTreeManager.GetClaimFilePath(treePath);
        Assert.True(File.Exists(markerPath));
        Assert.True(File.Exists(claimPath));
        WorkingTreeOwnership marker = JsonSerializer.Deserialize<WorkingTreeOwnership>(File.ReadAllText(markerPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        WorkingTreeOwnership claim = JsonSerializer.Deserialize<WorkingTreeOwnership>(File.ReadAllText(claimPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(WorkingTreeManager.OwnershipVersion, marker.Version);
        Assert.NotEqual(Guid.Empty, marker.ClaimId);
        Assert.Equal(Path.GetFullPath(treePath), marker.CanonicalPath);
        Assert.Equal(marker, claim);
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
        InformantFatalException ex = await Assert.ThrowsAsync<InformantFatalException>(manager.EnsureFreshTreeAsync);
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

        await Assert.ThrowsAsync<InformantFatalException>(manager.EnsureFreshTreeAsync);

        // The refusal must have fired before any destructive git command ran
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(treePath, "a.txt")));
    }

    [Fact]
    public async Task RefusesTreeWhoseExternalClaimVanished()
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            File.Delete(WorkingTreeManager.GetClaimFilePath(treePath));
            await AssertRefusesWithoutResetAsync(manager, treePath);
        }
    }

    [Fact]
    public async Task RefusesOwnershipMarkerSymbolicLinkBeforeReset()
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            string markerPath = Path.Combine(treePath, WorkingTreeManager.MarkerFileName);
            File.Delete(markerPath);
            bool created = TryCreateFileSymbolicLink(markerPath, WorkingTreeManager.GetClaimFilePath(treePath));
            Assert.SkipUnless(created, "symbolic links are not available on this test host");
            try
            {
                await AssertRefusesWithoutResetAsync(manager, treePath);
            }
            finally
            {
                File.Delete(markerPath);
            }
        }
    }

    [Fact]
    public async Task RefusesMarkerCopiedFromAnotherTree()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("a.txt", "original\n", "seed");
        string firstTree = Path.Combine(repository.Root, "tree-one");
        string secondTree = Path.Combine(repository.Root, "tree-two");
        WorkingTreeManager first = CreateManager(repository, firstTree);
        WorkingTreeManager second = CreateManager(repository, secondTree);
        await first.EnsureFreshTreeAsync();
        await second.EnsureFreshTreeAsync();
        File.Copy(Path.Combine(firstTree, WorkingTreeManager.MarkerFileName), Path.Combine(secondTree, WorkingTreeManager.MarkerFileName), overwrite: true);

        await AssertRefusesWithoutResetAsync(second, secondTree);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("repository")]
    [InlineData("branch")]
    [InlineData("claimId")]
    public async Task RefusesInvalidOwnershipFieldsBeforeReset(string field)
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            string markerPath = Path.Combine(treePath, WorkingTreeManager.MarkerFileName);
            string claimPath = WorkingTreeManager.GetClaimFilePath(treePath);
            JsonObject marker = ReadObject(markerPath);
            JsonObject claim = ReadObject(claimPath);
            switch (field)
            {
                case "path":
                    marker["canonicalPath"] = Path.Combine(repository.Root, "somewhere-else");
                    claim["canonicalPath"] = Path.Combine(repository.Root, "somewhere-else");
                    break;

                case "repository":
                    marker["repositoryUrl"] = "https://example.test/wrong.git";
                    claim["repositoryUrl"] = "https://example.test/wrong.git";
                    break;

                case "branch":
                    marker["branch"] = "wrong-branch";
                    claim["branch"] = "wrong-branch";
                    break;

                case "claimId":
                    marker["claimId"] = Guid.NewGuid();
                    break;
            }

            File.WriteAllText(markerPath, marker.ToJsonString());
            File.WriteAllText(claimPath, claim.ToJsonString());
            await AssertRefusesWithoutResetAsync(manager, treePath);
        }
    }

    [Theory]
    [InlineData("This directory is owned by Informant")]
    [InlineData("{ not json")]
    public async Task RefusesLegacyOrMalformedMarkerBeforeReset(string markerContent)
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            File.WriteAllText(Path.Combine(treePath, WorkingTreeManager.MarkerFileName), markerContent);
            await AssertRefusesWithoutResetAsync(manager, treePath);
        }
    }

    [Fact]
    public async Task RefusesWrongOriginBeforeReset()
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            await new GitRunner(TestGit.ExecutablePath).RunCheckedAsync("-C", treePath, "remote", "set-url", "origin", "https://example.test/wrong.git");
            await AssertRefusesWithoutResetAsync(manager, treePath);
        }
    }

    [Fact]
    public async Task RefusesWrongCheckedOutBranchBeforeReset()
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            await new GitRunner(TestGit.ExecutablePath).RunCheckedAsync("-C", treePath, "checkout", "-b", "wrong-branch");
            await AssertRefusesWithoutResetAsync(manager, treePath);
        }
    }

    [Fact]
    public async Task RefusesMissingGitDirectoryBeforeReset()
    {
        (TestRepository repository, string treePath, WorkingTreeManager manager) = await CreateClaimedTreeAsync();
        using (repository)
        {
            string gitPath = Path.Combine(treePath, ".git");
            string movedGitPath = Path.Combine(treePath, ".git-moved");
            Directory.Move(gitPath, movedGitPath);
            try
            {
                File.WriteAllText(Path.Combine(treePath, "a.txt"), "must survive");
                await Assert.ThrowsAsync<InformantFatalException>(manager.EnsureFreshTreeAsync);
                Assert.Equal("must survive", File.ReadAllText(Path.Combine(treePath, "a.txt")));
            }
            finally
            {
                Directory.Move(movedGitPath, gitPath);
            }
        }
    }

    private static WorkingTreeManager CreateManager(TestRepository repository, string treePath) => new(new GitRunner(TestGit.ExecutablePath), repository.RemotePath, "main", treePath);

    private static async Task<(TestRepository Repository, string TreePath, WorkingTreeManager Manager)> CreateClaimedTreeAsync()
    {
        TestRepository repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("a.txt", "original\n", "seed");
        string treePath = Path.Combine(repository.Root, "tree");
        WorkingTreeManager manager = CreateManager(repository, treePath);
        await manager.EnsureFreshTreeAsync();
        return (repository, treePath, manager);
    }

    private static async Task AssertRefusesWithoutResetAsync(WorkingTreeManager manager, string treePath)
    {
        File.WriteAllText(Path.Combine(treePath, "a.txt"), "must survive");
        await Assert.ThrowsAsync<InformantFatalException>(manager.EnsureFreshTreeAsync);
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(treePath, "a.txt")));
    }

    private static JsonObject ReadObject(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static bool TryCreateFileSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
