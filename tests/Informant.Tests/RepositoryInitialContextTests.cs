namespace Informant.Tests;

/// <summary>Tests bounded, manifest-verified source selected for repository planning</summary>
public sealed class RepositoryInitialContextTests
{
    [Fact]
    public void SelectsRootGuidanceBuildSeedsAndChangedSourceWithinBudget()
    {
        using var directory = new TempDirectory();
        Write(directory.Path, "README.md", "root-guidance-token");
        Write(directory.Path, "package.json", "root-build-token");
        Write(directory.Path, "src/Seed.cs", "seed-token");
        Write(directory.Path, "src/Changed.cs", "changed-token");
        Write(directory.Path, "src/Other.cs", "ordinary-token");

        RepositoryManifest manifest = Manifest(directory.Path,
            Entry(directory.Path, "README.md"),
            Entry(directory.Path, "package.json"),
            Entry(directory.Path, "src/Seed.cs"),
            Entry(directory.Path, "src/Changed.cs", ChangeKind.Modified),
            Entry(directory.Path, "src/Other.cs"));
        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(manifest, ["src/Seed.cs"], 2000);

        RepositoryInitialContext context = new RepositoryInitialContextBuilder(1024 * 1024).Build(manifest, briefing, 16000);

        Assert.Equal(["README.md", "package.json", "src/Seed.cs", "src/Changed.cs"], context.Items.Select(item => item.Id));
        Assert.Contains("root-guidance-token", context.Items[0].Content);
        Assert.Contains("root-build-token", context.Items[1].Content);
        Assert.Contains("seed-token", context.Items[2].Content);
        Assert.Contains("changed-token", context.Items[3].Content);
        Assert.DoesNotContain(context.Items, item => item.Id == "src/Other.cs");
        Assert.True(context.UsedCharacters <= context.CharacterBudget);
    }

    [Fact]
    public void OmitsWholeBlocksThatDoNotFitInsteadOfTruncatingThem()
    {
        using var directory = new TempDirectory();
        Write(directory.Path, "README.md", new string('x', 4000));
        RepositoryManifest manifest = Manifest(directory.Path, Entry(directory.Path, "README.md"));
        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(manifest, [], 2000);

        RepositoryInitialContext context = new RepositoryInitialContextBuilder(1024 * 1024).Build(manifest, briefing, 16000);

        Assert.Empty(context.Items);
        RepositoryInitialContextOmission omission = Assert.Single(context.Omissions);
        Assert.Equal("README.md", omission.Path);
        Assert.Equal("Budget", omission.ReasonCode);
    }

    [Fact]
    public void RejectsSourceChangedAfterManifestCreation()
    {
        using var directory = new TempDirectory();
        Write(directory.Path, "README.md", "original-content");
        RepositoryManifest manifest = Manifest(directory.Path, Entry(directory.Path, "README.md"));
        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(manifest, [], 2000);
        Write(directory.Path, "README.md", "modified-content");

        RepositoryInitialContext context = new RepositoryInitialContextBuilder(1024 * 1024).Build(manifest, briefing, 16000);

        Assert.Empty(context.Items);
        RepositoryInitialContextOmission omission = Assert.Single(context.Omissions);
        Assert.Equal("README.md", omission.Path);
        Assert.Equal("ChangedSinceManifest", omission.ReasonCode);
    }

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static RepositoryManifest Manifest(string root, params RepositoryManifestEntry[] entries) =>
        new("repository", "main", root, "baseline", "tip", ReviewMode.Changed, "run", DateTimeOffset.UnixEpoch, entries);

    private static RepositoryManifestEntry Entry(string root, string path, ChangeKind? changeKind = null)
    {
        var reader = new RepositoryFileReader(root);
        RepositoryFileInspection inspection = reader.Inspect(path);
        return new RepositoryManifestEntry(path, "100644", "blob", "object", inspection.SizeBytes, inspection.LineCount, inspection.ContentHash, Path.GetExtension(path), !path.Contains('/'),
            RepositoryManifestDisposition.Text, changeKind);
    }
}
