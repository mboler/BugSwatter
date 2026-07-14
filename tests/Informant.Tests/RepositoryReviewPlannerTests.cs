namespace Informant.Tests;

/// <summary>Tests bounded planning batches and deterministic exhaustive fallback</summary>
public sealed class RepositoryReviewPlannerTests
{
    /// <summary>Verifies a valid model grouping remains exhaustive and is assigned a batch-qualified identifier</summary>
    [Fact]
    public async Task ValidPlanGroupsRelatedCandidates()
    {
        RepositoryManifest manifest = Manifest(Text("src/One.cs"), Text("src/Two.cs"), Text("README.md"));
        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(manifest, [], 2000);
        var planner = new RepositoryReviewPlanner(16000);
        int calls = 0;

        RepositoryPlanningResult result = await planner.PlanAsync(manifest, briefing, ["src/One.cs", "src/Two.cs"], ["src/One.cs", "src/Two.cs"], false,
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult("""
                    {
                      "version": 1,
                      "repositorySummary": "related source",
                      "units": [
                        {
                          "id": "source",
                          "priority": 1,
                          "rationale": "same directory",
                          "paths": ["src/One.cs", "src/Two.cs"],
                          "supportingPaths": ["README.md"]
                        }
                      ],
                      "deferred": [],
                      "uncertainties": []
                    }
                    """);
            });

        RepositoryReviewUnit unit = Assert.Single(result.Plan.Units);
        Assert.Equal("batch-001-source", unit.Id);
        Assert.Equal(["src/One.cs", "src/Two.cs"], unit.Paths);
        Assert.False(result.Plan.UsedFallback);
        Assert.Equal(1, calls);
    }

    /// <summary>Verifies malformed planning output falls back without omitting any exhaustive candidate</summary>
    [Fact]
    public async Task MalformedPlanFallsBackToCompleteCandidateCoverage()
    {
        RepositoryManifest manifest = Manifest(Text("a.cs"), Text("folder/b.py"));
        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(manifest, [], 2000);
        var planner = new RepositoryReviewPlanner(16000);

        RepositoryPlanningResult result = await planner.PlanAsync(manifest, briefing, ["a.cs", "folder/b.py"], ["a.cs", "folder/b.py"], false,
            (_, _, _) => Task.FromResult("not json"));

        Assert.True(result.Plan.UsedFallback);
        Assert.Equal(["a.cs", "folder/b.py"], result.Plan.Units.SelectMany(unit => unit.Paths).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Empty(result.Plan.Deferred);
    }

    /// <summary>Verifies an oversized manifest partition bypasses the model and uses deterministic grouping</summary>
    [Fact]
    public async Task OversizedPlanningInputDoesNotCallModel()
    {
        string path = $"folder/{new string('x', 400)}.cs";
        RepositoryManifest manifest = Manifest(Text(path));
        RepositoryBriefing briefing = new RepositoryBriefingBuilder().Build(manifest, [], 256);
        var planner = new RepositoryReviewPlanner(8000);
        int calls = 0;

        RepositoryPlanningResult result = await planner.PlanAsync(manifest, briefing, [path], [path], false,
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult("{}");
            });

        Assert.Equal(0, calls);
        Assert.True(result.Plan.UsedFallback);
        Assert.Equal(path, Assert.Single(result.Plan.Units).Paths[0]);
    }

    private static RepositoryManifest Manifest(params RepositoryManifestEntry[] entries) =>
        new("repository", "main", "tree", "baseline", "tip", ReviewMode.Changed, "run", DateTimeOffset.UnixEpoch, entries);

    private static RepositoryManifestEntry Text(string path) => new(path, "100644", "blob", "object", 100, 10, "hash", Path.GetExtension(path), !path.Contains('/'),
        RepositoryManifestDisposition.Text, ChangeKind.Modified);
}
