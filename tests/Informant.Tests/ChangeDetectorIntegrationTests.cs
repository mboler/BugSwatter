using System.Text.Json;

namespace Informant.Tests;

public sealed class ChangeDetectorIntegrationTests
{
    [Fact]
    public async Task DetectsModifiedAddedDeletedWithExactRanges()
    {
        using var repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("a.txt", "l1\nl2\nl3\nl4\nl5\n", "base a");
        await repository.CommitFileAsync("b.txt", "gone soon\n", "base b");
        baseline = await repository.CommitFileAsync("keep.txt", "stays\n", "base keep");

        await repository.CommitFileAsync("a.txt", "l1\nl2\nCHANGED\nl4\nl5\nl6 appended\nl7 appended\n", "modify a");
        await repository.CommitFileAsync("c.txt", "new1\nnew2\nnew3\n", "add c");
        string tip = await repository.DeleteFileAsync("b.txt", "delete b");

        WorkingTreeManager manager = await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));
        IReadOnlyList<ChangedFile> files = await detector.GetChangedFilesAsync(baseline, tip);

        Assert.Equal(3, files.Count);
        ChangedFile a = Assert.Single(files, f => f.Path == "a.txt");
        Assert.Equal(ChangeKind.Modified, a.Kind);
        Assert.Equal([new LineRange(3, 3), new LineRange(6, 7)], a.ChangedRanges);
        ChangedFile c = Assert.Single(files, f => f.Path == "c.txt");
        Assert.Equal(ChangeKind.Added, c.Kind);
        Assert.Equal([new LineRange(1, 3)], c.ChangedRanges);
        ChangedFile deleted = Assert.Single(files, f => f.Path == "b.txt");
        Assert.Equal(ChangeKind.Deleted, deleted.Kind);
        Assert.Equal(baseline, deleted.ContentRevision);
        Assert.Empty(deleted.ChangedRanges);
        Assert.Equal(tip, await manager.GetTipShaAsync());
    }

    [Fact]
    public async Task DetectsRenameWithEditRangesOnly()
    {
        using var repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("old-name.txt", "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\n", "base");
        await repository.RenameFileAsync("old-name.txt", "new-name.txt", "rename");
        string tip = await repository.CommitFileAsync("new-name.txt", "one\ntwo\nthree\nEDITED\nfive\nsix\nseven\neight\nnine\nten\n", "edit after rename");

        await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));
        IReadOnlyList<ChangedFile> files = await detector.GetChangedFilesAsync(baseline, tip);

        ChangedFile renamed = Assert.Single(files);
        Assert.Equal(ChangeKind.Renamed, renamed.Kind);
        Assert.Equal("new-name.txt", renamed.Path);
        Assert.Equal([new LineRange(4, 4)], renamed.ChangedRanges);
    }

    [Fact]
    public async Task DeletionOnlyCommitProducesReviewableDeletion()
    {
        using var repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("removed.txt", "content that must be reviewed before removal\n", "add file");
        string tip = await repository.DeleteFileAsync("removed.txt", "delete file");
        await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));

        ChangedFile deleted = Assert.Single(await detector.GetChangedFilesAsync(baseline, tip));

        Assert.Equal("removed.txt", deleted.Path);
        Assert.Equal(ChangeKind.Deleted, deleted.Kind);
        Assert.Equal(baseline, deleted.ContentRevision);
    }

    [Fact]
    public async Task PureRenameYieldsNoRanges()
    {
        using var repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("before.txt", "same content\nacross the rename\nfor all lines\n", "base");
        await repository.RenameFileAsync("before.txt", "after.txt", "pure rename");

        WorkingTreeManager manager = await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));
        IReadOnlyList<ChangedFile> files = await detector.GetChangedFilesAsync(baseline, await manager.GetTipShaAsync());

        ChangedFile renamed = Assert.Single(files);
        Assert.Equal(ChangeKind.Renamed, renamed.Kind);
        Assert.Empty(renamed.ChangedRanges);
    }

    [Fact]
    public async Task GlobCharactersInFilenamesAreTreatedLiterally()
    {
        using var repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("data[1].txt", "one\ntwo\nthree\n", "bracketed file");
        await repository.CommitFileAsync("data1.txt", "decoy\n", "decoy that a glob pathspec would match");
        string tip = await repository.CommitFileAsync("data[1].txt", "one\nCHANGED\nthree\n", "edit bracketed file");

        await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));
        IReadOnlyList<ChangedFile> files = await detector.GetChangedFilesAsync(baseline, tip);

        ChangedFile bracketed = Assert.Single(files, f => f.Path == "data[1].txt");
        Assert.Equal([new LineRange(2, 2)], bracketed.ChangedRanges);
    }

    [Fact]
    public async Task FullListingReturnsAllTrackedFiles()
    {
        using var repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("a.txt", "a\n", "a");
        await repository.CommitFileAsync("sub/b.txt", "b\n", "b");
        await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));
        IReadOnlyList<ChangedFile> files = await detector.GetAllFilesAsync();
        Assert.Equal(["a.txt", "sub/b.txt"], files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.All(files, f => Assert.Equal(ChangeKind.FullReview, f.Kind));
    }

    [Fact]
    public async Task FullListingPreservesHostSupportedUnusualNames()
    {
        using var repository = await TestRepository.CreateAsync();
        List<string> paths = [" leading.txt", "unicodé-文件.txt"];
        if (!OperatingSystem.IsWindows())
        {
            paths.AddRange(["tab\tname.txt", "trailing.txt ", "line\nbreak.txt"]);
        }

        foreach (string path in paths)
        {
            await repository.CommitFileAsync(path, "content\n", $"add {path}");
        }

        await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));

        IReadOnlyList<ChangedFile> files = await detector.GetAllFilesAsync();

        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), files.Select(file => file.Path).OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReportsBaselineReachability()
    {
        using var repository = await TestRepository.CreateAsync();
        string sha = await repository.CommitFileAsync("a.txt", "a\n", "a");
        await CloneTreeAsync(repository);
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "tree"));
        Assert.True(await detector.IsCommitReachableAsync(sha));
        Assert.False(await detector.IsCommitReachableAsync("0123456789abcdef0123456789abcdef01234567"));
    }

    [Fact]
    public async Task BaselineVerificationPropagatesUnexpectedGitFailures()
    {
        using var repository = await TestRepository.CreateAsync();
        string sha = await repository.CommitFileAsync("a.txt", "a\n", "a");
        var detector = new ChangeDetector(new GitRunner(TestGit.ExecutablePath), Path.Combine(repository.Root, "missing-tree"));

        GitOperationException exception = await Assert.ThrowsAsync<GitOperationException>(() => detector.IsCommitReachableAsync(sha));

        Assert.Contains("Could not verify baseline commit", exception.Message);
    }

    [Fact]
    public async Task ChangeSetFilePersistsInspectableJson()
    {
        using var directory = new TempDirectory();
        ChangedFile[] files = [new ChangedFile("src/Foo.cs", ChangeKind.Modified, [new LineRange(3, 5)]), new ChangedFile("src/Bar.cs", ChangeKind.Added, [new LineRange(1, 10)])];
        string path = ChangeSetFile.Write(directory.Path, "2026-07-10_12-00-00", "baseSha", "tipSha", ReviewMode.Changed, files);
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("baseSha", document.RootElement.GetProperty("baselineSha").GetString());
        Assert.Equal("tipSha", document.RootElement.GetProperty("tipSha").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("fileCount").GetInt32());
        JsonElement first = document.RootElement.GetProperty("files")[0];
        Assert.Equal("src/Foo.cs", first.GetProperty("path").GetString());
        Assert.Equal("Modified", first.GetProperty("kind").GetString());
        Assert.Equal(3, first.GetProperty("changedRanges")[0].GetProperty("start").GetInt32());
        Assert.Equal(5, first.GetProperty("changedRanges")[0].GetProperty("end").GetInt32());
    }

    private static async Task<WorkingTreeManager> CloneTreeAsync(TestRepository repository)
    {
        var manager = new WorkingTreeManager(new GitRunner(TestGit.ExecutablePath), repository.RemotePath, "main", Path.Combine(repository.Root, "tree"));
        await manager.EnsureFreshTreeAsync();
        return manager;
    }
}
