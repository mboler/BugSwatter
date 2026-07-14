using System.Text.Json;

namespace Informant.Tests;

/// <summary>Tests untrusted model-plan validation, deterministic fallback, and mandatory coverage repair</summary>
public sealed class RepositoryReviewPlanValidatorTests
{
    /// <summary>Verifies a valid adaptive plan preserves explicit deferrals and repairs every omitted non-deferred candidate</summary>
    [Fact]
    public void ValidAdaptivePlanRepairsOmittedCoverage()
    {
        RepositoryManifest manifest = CreateManifest();
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            repositorySummary = "Mixed repository",
            units = new[]
            {
                new { id = "core", priority = 1, rationale = "core change", paths = new[] { "src/A.cs" }, supportingPaths = new[] { "README.md" } }
            },
            deferred = new[] { new { path = "docs/guide.md", reason = "documentation is outside this adaptive pass" } },
            uncertainties = new[] { "Runtime wiring is unclear" }
        });

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, manifest, Candidates, ["src/A.cs", "tests/A.cs"], allowDeferrals: true);

        Assert.False(plan.UsedFallback);
        Assert.True(plan.CoverageRepaired);
        Assert.Equal("docs/guide.md", Assert.Single(plan.Deferred).Path);
        Assert.Equal(["src/A.cs", "src/B.cs", "tests/A.cs"], plan.Units.SelectMany(unit => unit.Paths).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(["README.md"], plan.Units[0].SupportingPaths);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Contains("coverage repair", StringComparison.Ordinal));
    }

    /// <summary>Verifies a model cannot defer mandatory changed content and the controller assigns it to repair work</summary>
    [Fact]
    public void MandatoryDeferralBecomesCoverageRepair()
    {
        RepositoryManifest manifest = CreateManifest();
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            repositorySummary = "Attempted mandatory deferral",
            units = new[] { new { id = "core", priority = 1, rationale = "core", paths = new[] { "src/A.cs" }, supportingPaths = Array.Empty<string>() } },
            deferred = new[] { new { path = "tests/A.cs", reason = "too expensive" } },
            uncertainties = Array.Empty<string>()
        });

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, manifest, ["src/A.cs", "tests/A.cs"], ["src/A.cs", "tests/A.cs"], allowDeferrals: true);

        Assert.False(plan.UsedFallback);
        Assert.Empty(plan.Deferred);
        Assert.Contains(plan.Units.SelectMany(unit => unit.Paths), path => path == "tests/A.cs");
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Contains("could not be deferred", StringComparison.Ordinal));
    }

    /// <summary>Verifies malformed, empty, and unsupported planning responses fall back to complete deterministic candidate coverage</summary>
    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"repositorySummary\":\"wrong\",\"units\":[]}")]
    [InlineData("{\"version\":1,\"repositorySummary\":\"empty\",\"units\":[]}")]
    public void UnusablePlanFallsBackToEveryCandidate(string json)
    {
        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, CreateManifest(), Candidates, ["src/A.cs"], allowDeferrals: true);

        Assert.True(plan.UsedFallback);
        Assert.Equal(Candidates.OrderBy(path => path, StringComparer.Ordinal), plan.Units.SelectMany(unit => unit.Paths).OrderBy(path => path, StringComparer.Ordinal));
        Assert.NotEmpty(plan.Diagnostics);
    }

    /// <summary>Verifies invented, traversal, and excluded paths invalidate the model plan instead of entering a review unit</summary>
    [Theory]
    [InlineData("invented.cs")]
    [InlineData("../outside.cs")]
    [InlineData("linked")]
    public void UnsafeUnknownOrExcludedPathForcesFallback(string path)
    {
        string json = PlanWithPaths(path);

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, CreateManifest(), Candidates, ["src/A.cs"], allowDeferrals: true);

        Assert.True(plan.UsedFallback);
        Assert.DoesNotContain(path, plan.Units.SelectMany(unit => unit.Paths));
    }

    /// <summary>Verifies primary paths cannot be assigned to multiple review units</summary>
    [Fact]
    public void DuplicatePrimaryAssignmentForcesFallback()
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            repositorySummary = "duplicate",
            units = new[]
            {
                new { id = "one", priority = 1, rationale = "one", paths = new[] { "src/A.cs" }, supportingPaths = Array.Empty<string>() },
                new { id = "two", priority = 2, rationale = "two", paths = new[] { "src/A.cs" }, supportingPaths = Array.Empty<string>() }
            },
            deferred = Array.Empty<object>(),
            uncertainties = Array.Empty<string>()
        });

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, CreateManifest(), Candidates, ["src/A.cs"], allowDeferrals: true);

        Assert.True(plan.UsedFallback);
        Assert.Contains("more than once", Assert.Single(plan.Diagnostics));
    }

    /// <summary>Verifies exhaustive planning rejects any proposed deferral and falls back to complete coverage</summary>
    [Fact]
    public void ExhaustivePlanCannotDefer()
    {
        string json = JsonSerializer.Serialize(new
        {
            version = 1,
            repositorySummary = "exhaustive",
            units = new[] { new { id = "one", priority = 1, rationale = "one", paths = new[] { "src/A.cs" }, supportingPaths = Array.Empty<string>() } },
            deferred = new[] { new { path = "docs/guide.md", reason = "skip" } },
            uncertainties = Array.Empty<string>()
        });

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, CreateManifest(), Candidates, Candidates, allowDeferrals: false);

        Assert.True(plan.UsedFallback);
        Assert.Empty(plan.Deferred);
        Assert.Equal(Candidates.Count, plan.Units.Sum(unit => unit.Paths.Count));
    }

    /// <summary>Verifies an oversized model response is rejected before JSON parsing and receives deterministic fallback</summary>
    [Fact]
    public void PlanCharacterLimitForcesFallback()
    {
        RepositoryPlanLimits limits = new(MaxPlanCharacters: 10);

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(PlanWithPaths("src/A.cs"), CreateManifest(), Candidates, ["src/A.cs"], true, limits);

        Assert.True(plan.UsedFallback);
        Assert.Contains("exceeded", Assert.Single(plan.Diagnostics));
    }

    /// <summary>Verifies null elements in model-generated unit arrays are handled as invalid output rather than crashing validation</summary>
    [Fact]
    public void NullUnitForcesFallback()
    {
        const string json = """
            { "version": 1, "repositorySummary": "null unit", "units": [null], "deferred": [], "uncertainties": [] }
            """;

        RepositoryReviewPlan plan = RepositoryReviewPlanValidator.ValidateOrFallback(json, CreateManifest(), Candidates, ["src/A.cs"], true);

        Assert.True(plan.UsedFallback);
        Assert.Contains("null review unit", Assert.Single(plan.Diagnostics));
    }

    private static IReadOnlyList<string> Candidates => ["src/A.cs", "src/B.cs", "tests/A.cs", "docs/guide.md"];

    private static string PlanWithPaths(params string[] paths) => JsonSerializer.Serialize(new
    {
        version = 1,
        repositorySummary = "plan",
        units = new[] { new { id = "one", priority = 1, rationale = "one", paths, supportingPaths = Array.Empty<string>() } },
        deferred = Array.Empty<object>(),
        uncertainties = Array.Empty<string>()
    });

    private static RepositoryManifest CreateManifest() => new("repository", "main", "tree", "baseline", "tip", ReviewMode.Changed, "run", DateTimeOffset.UnixEpoch,
    [
        Text("README.md"),
        Text("src/A.cs", ChangeKind.Modified),
        Text("src/B.cs"),
        Text("tests/A.cs", ChangeKind.Modified),
        Text("docs/guide.md"),
        new RepositoryManifestEntry("linked", "120000", "blob", "object", null, null, null, "", true, RepositoryManifestDisposition.SymbolicLink)
    ]);

    private static RepositoryManifestEntry Text(string path, ChangeKind? changeKind = null) => new(path, "100644", "blob", "object", 100, 10, "hash", Path.GetExtension(path),
        !path.Contains('/'), RepositoryManifestDisposition.Text, changeKind);
}
