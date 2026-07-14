namespace BugSwatter.Git.Tests;

/// <summary>Tests immutable Git tree enumeration and parsing</summary>
public sealed class GitTreeCatalogTests
{
    /// <summary>Verifies mode classification without trimming legal filename characters</summary>
    [Fact]
    public void ParsePreservesFilenamesAndClassifiesGitModes()
    {
        string output = "120000 blob link-object\tlinks/model\0"
            + "100755 blob script-object\tscripts/run\tcarefully\nnow.sh\0"
            + "160000 commit module-object\tvendor/module\0"
            + "040000 tree tree-object\tunsupported\0";

        IReadOnlyList<GitTreeEntry> entries = GitTreeCatalog.Parse(output);

        Assert.Collection(entries,
            entry =>
            {
                Assert.Equal("links/model", entry.Path);
                Assert.Equal(GitTreeEntryKind.SymbolicLink, entry.Kind);
            },
            entry =>
            {
                Assert.Equal("scripts/run\tcarefully\nnow.sh", entry.Path);
                Assert.Equal(GitTreeEntryKind.RegularFile, entry.Kind);
            },
            entry =>
            {
                Assert.Equal("unsupported", entry.Path);
                Assert.Equal(GitTreeEntryKind.Unsupported, entry.Kind);
            },
            entry =>
            {
                Assert.Equal("vendor/module", entry.Path);
                Assert.Equal(GitTreeEntryKind.GitLink, entry.Kind);
            });
    }

    /// <summary>Verifies malformed Git output fails closed</summary>
    [Fact]
    public void ParseRejectsMalformedRecords()
    {
        Assert.Throws<GitOperationException>(() => GitTreeCatalog.Parse("100644 blob object-without-a-path\0"));
        Assert.Throws<GitOperationException>(() => GitTreeCatalog.Parse("100644 blob\tpath.cs\0"));
    }

    /// <summary>Verifies catalog enumeration is bound to the requested revision</summary>
    [Fact]
    public async Task ListReadsTheRequestedImmutableRevision()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        string firstTip = await repository.CommitFileAsync("first.cs", "class First { }", "first");
        string secondTip = await repository.CommitFileAsync("second.cs", "class Second { }", "second");
        var catalog = new GitTreeCatalog(new GitRunner(TestGit.ExecutablePath), repository.SeedPath);

        IReadOnlyList<GitTreeEntry> firstEntries = await catalog.ListAsync(firstTip);
        IReadOnlyList<GitTreeEntry> secondEntries = await catalog.ListAsync(secondTip);

        Assert.Equal(["first.cs"], firstEntries.Select(entry => entry.Path));
        Assert.Equal(["first.cs", "second.cs"], secondEntries.Select(entry => entry.Path));
    }
}
