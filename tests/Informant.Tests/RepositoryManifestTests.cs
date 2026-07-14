using System.Text.Json;

namespace Informant.Tests;

/// <summary>Tests repository manifest construction, change annotation, and persistence</summary>
public sealed class RepositoryManifestTests
{
    /// <summary>Verifies each build reflects the requested immutable tip and refreshed working files</summary>
    [Fact]
    public async Task RebuildReflectsTheRequestedTipAndCurrentFileMetadata()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        string firstTip = await repository.CommitFileAsync("src/Program.cs", "first\nsecond\n", "first version");
        var builder = CreateBuilder(repository, 1024);

        RepositoryManifest first = await builder.BuildAsync(repository.RemotePath, "main", null, firstTip, ReviewMode.Changed, "1970-01-01_00-00-00", DateTimeOffset.UnixEpoch);
        string secondTip = await repository.CommitFileAsync("src/Program.cs", "first\nsecond\nthird\n", "second version");
        RepositoryManifest second = await builder.BuildAsync(repository.RemotePath, "main", firstTip, secondTip, ReviewMode.Changed, "1970-01-01_00-01-00", DateTimeOffset.UnixEpoch.AddMinutes(1));

        RepositoryManifestEntry firstEntry = Assert.Single(first.Entries);
        RepositoryManifestEntry secondEntry = Assert.Single(second.Entries);
        Assert.Equal(2, firstEntry.LineCount);
        Assert.Equal(3, secondEntry.LineCount);
        Assert.NotEqual(firstEntry.GitObjectId, secondEntry.GitObjectId);
        Assert.Equal(firstTip, first.TipSha);
        Assert.Equal(secondTip, second.TipSha);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.SeedPath)), second.WorkingTreeRoot);
        Assert.Equal("1970-01-01_00-01-00", second.RunStamp);
        Assert.False(secondEntry.RootLevel);
        Assert.Equal(".cs", secondEntry.Extension);
    }

    /// <summary>Verifies unsafe source categories are represented only as metadata</summary>
    [Fact]
    public async Task BuildClassifiesBinaryAndOversizedFilesWithoutReadingThemAsSource()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        await repository.CommitFileAsync("binary.dat", "before\0after", "binary");
        string tip = await repository.CommitFileAsync("large.txt", new string('x', 100), "large");

        RepositoryManifest manifest = await CreateBuilder(repository, 32).BuildAsync(repository.RemotePath, "main", null, tip, ReviewMode.Full, "1970-01-01_00-00-00", DateTimeOffset.UnixEpoch);

        Assert.Collection(manifest.Entries,
            entry =>
            {
                Assert.Equal("binary.dat", entry.Path);
                Assert.Equal(RepositoryManifestDisposition.Binary, entry.Disposition);
                Assert.False(entry.Reviewable);
            },
            entry =>
            {
                Assert.Equal("large.txt", entry.Path);
                Assert.Equal(RepositoryManifestDisposition.TooLarge, entry.Disposition);
                Assert.False(entry.Reviewable);
            });
    }

    /// <summary>Verifies current changes and deleted baseline content are represented accurately</summary>
    [Fact]
    public void WithChangesAnnotatesCurrentEntriesAndAddsDeletedTombstones()
    {
        var current = new RepositoryManifestEntry("current.cs", "100644", "blob", "object", 10, 1, "hash", ".cs", true, RepositoryManifestDisposition.Text);
        var manifest = new RepositoryManifest("repository", "main", "tree", "baseline", "tip", ReviewMode.Changed, "1970-01-01_00-00-00", DateTimeOffset.UnixEpoch, [current]);
        ChangedFile[] changes =
        [
            new ChangedFile("current.cs", ChangeKind.Modified, [new LineRange(1, 1)]),
            new ChangedFile("deleted.cs", ChangeKind.Deleted, [], "baseline")
        ];

        RepositoryManifest result = manifest.WithChanges(changes);

        Assert.Collection(result.Entries,
            entry =>
            {
                Assert.Equal("current.cs", entry.Path);
                Assert.Equal(ChangeKind.Modified, entry.ChangeKind);
                Assert.Equal(RepositoryManifestDisposition.Text, entry.Disposition);
            },
            entry =>
            {
                Assert.Equal("deleted.cs", entry.Path);
                Assert.Equal(ChangeKind.Deleted, entry.ChangeKind);
                Assert.Equal("baseline", entry.ContentRevision);
                Assert.Equal(RepositoryManifestDisposition.DeletedFromTip, entry.Disposition);
                Assert.True(entry.Reviewable);
            });
        Assert.Equal(2, result.SelectedCount);
    }

    /// <summary>Verifies persisted manifests contain inventory metadata rather than source bodies</summary>
    [Fact]
    public void ManifestFileContainsMetadataButNoSourceContent()
    {
        using var reports = new TempDirectory();
        var entry = new RepositoryManifestEntry("source.cs", "100644", "blob", "object", 20, 2, "hash", ".cs", true, RepositoryManifestDisposition.Text, ChangeKind.Modified);
        var manifest = new RepositoryManifest("repository", "main", "tree", "baseline", "tip", ReviewMode.Changed, "1970-01-01_00-00-00", DateTimeOffset.UnixEpoch, [entry]);

        string path = RepositoryManifestFile.Write(reports.Path, "2026-07-14_01-02-03", manifest);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("tip", document.RootElement.GetProperty("tipSha").GetString());
        Assert.Equal("1970-01-01_00-00-00", document.RootElement.GetProperty("runStamp").GetString());
        Assert.Equal("source.cs", document.RootElement.GetProperty("entries")[0].GetProperty("path").GetString());
        Assert.DoesNotContain("source content", json, StringComparison.Ordinal);
    }

    private static RepositoryManifestBuilder CreateBuilder(TestRepository repository, int maxFileBytes)
    {
        var git = new GitRunner(TestGit.ExecutablePath);
        return new RepositoryManifestBuilder(new GitTreeCatalog(git, repository.SeedPath), repository.SeedPath, maxFileBytes);
    }
}
