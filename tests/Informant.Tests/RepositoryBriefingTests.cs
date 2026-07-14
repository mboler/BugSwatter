namespace Informant.Tests;

/// <summary>Tests code-agnostic repository classification, seed matching, and manifest partitioning</summary>
public sealed class RepositoryBriefingTests
{
    /// <summary>Verifies guidance, build metadata, seeds, changes, and ordinary files are ranked without language-specific source parsing</summary>
    [Fact]
    public void ClassifiesMixedLanguageAndUnknownFilesDeterministically()
    {
        RepositoryManifest manifest = CreateManifest(
            Text("web/app.js"),
            Text("notes/custom.weird"),
            Text("src/App.cs", ChangeKind.Modified),
            Text("README.md"),
            Text("scripts/main.py"),
            Text("docs/setup/install.md"),
            Text("package.json"));
        var builder = new RepositoryBriefingBuilder();

        RepositoryBriefing briefing = builder.Build(manifest, ["scripts", "docs/**/*.md", "missing/**"], 300);

        Assert.Equal("README.md", briefing.Entries[0].ManifestEntry.Path);
        Assert.Equal(RepositoryBriefingRole.Guidance | RepositoryBriefingRole.RepositoryContent, briefing.Entries[0].Roles);
        Assert.Equal("package.json", briefing.Entries[1].ManifestEntry.Path);
        Assert.True(briefing.Entries[1].Roles.HasFlag(RepositoryBriefingRole.BuildManifest));
        Assert.Equal(["docs/setup/install.md", "scripts/main.py"], briefing.Entries.Where(entry => entry.Priority == 30).Select(entry => entry.ManifestEntry.Path));
        Assert.True(briefing.Entries.Single(entry => entry.ManifestEntry.Path == "src/App.cs").Roles.HasFlag(RepositoryBriefingRole.Changed));
        Assert.True(briefing.Entries.Single(entry => entry.ManifestEntry.Path == "notes/custom.weird").Roles.HasFlag(RepositoryBriefingRole.RepositoryContent));
        Assert.Equal(["missing/**"], briefing.UnmatchedSeedPaths);
        Assert.All(briefing.ManifestPartitions, partition => Assert.True(partition.WithinCharacterLimit));
        Assert.All(briefing.ManifestPartitions, partition => Assert.InRange(partition.Text.Length, 1, 300));
        Assert.Contains("src/", briefing.DirectorySummary);
        Assert.Contains("7 paths", briefing.Summary);
    }

    /// <summary>Verifies identical manifest entries produce identical bounded partitions regardless of input ordering</summary>
    [Fact]
    public void PartitioningIsStableAcrossInputOrdering()
    {
        RepositoryManifestEntry[] entries = [Text("zeta/z.cs"), Text("alpha/a.cs"), Text("README.md"), Text("beta/b.py")];
        var builder = new RepositoryBriefingBuilder();

        RepositoryBriefing first = builder.Build(CreateManifest(entries), [], 280);
        RepositoryBriefing second = builder.Build(CreateManifest(entries.Reverse().ToArray()), [], 280);

        Assert.Equal(first.ManifestPartitions.Select(partition => partition.Text), second.ManifestPartitions.Select(partition => partition.Text));
        Assert.Equal(first.DirectorySummary, second.DirectorySummary);
    }

    /// <summary>Verifies a seed that selects a symbolic link is rejected rather than followed or silently treated as source</summary>
    [Fact]
    public void SymbolicLinkSeedIsRejected()
    {
        RepositoryManifest manifest = CreateManifest(new RepositoryManifestEntry("linked", "120000", "blob", "object", null, null, null, "", true,
            RepositoryManifestDisposition.SymbolicLink));

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => new RepositoryBriefingBuilder().Build(manifest, ["linked"], 1024));

        Assert.Contains("symbolic-link", exception.Message);
    }

    /// <summary>Verifies an exact path too large for one manifest partition is retained and explicitly marked over budget</summary>
    [Fact]
    public void OversizedManifestLineIsNotSilentlyTruncatedOrClaimedToFit()
    {
        string path = $"folder/{new string('x', 300)}.cs";

        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(CreateManifest(Text(path)), [], 256);

        RepositoryManifestPartition partition = Assert.Single(briefing.ManifestPartitions);
        Assert.False(partition.WithinCharacterLimit);
        Assert.Contains(path, partition.Text);
        Assert.Equal([path], partition.Paths);
    }

    private static RepositoryManifest CreateManifest(params RepositoryManifestEntry[] entries) =>
        new("repository", "main", "tree", "baseline", "tip", ReviewMode.Changed, "run", DateTimeOffset.UnixEpoch, entries);

    private static RepositoryManifestEntry Text(string path, ChangeKind? changeKind = null) => new(path, "100644", "blob", "object", 100, 10, "hash", Path.GetExtension(path),
        !path.Contains('/'), RepositoryManifestDisposition.Text, changeKind);
}
