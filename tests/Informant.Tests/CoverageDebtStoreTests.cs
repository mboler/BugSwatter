using System.Text.Json;

namespace Informant.Tests;

public sealed class CoverageDebtStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void UpdatePersistsDeferredDebtAndDeepReviewClearsIt()
    {
        string path = Path.Combine(_directory.Path, "coverage.json");
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        var store = new CoverageDebtStore(path, timeProvider);
        RepositoryManifest manifest = Manifest(Entry("src/Debt.cs", "object-1"));
        var file = new ChangedFile("src/Debt.cs", ChangeKind.FullReview, []);
        var deferred = new ReviewCoverageLedger(ReviewStrategy.Adaptive,
            [new ReviewCoverageEntry(file.Path, file.Kind, false, true, false, ReviewCoverageOutcome.Deferred, "budget exhausted")]);

        Assert.Equal(1, store.Update("repo", "main", manifest, deferred));
        CoverageDebtSelection selection = store.Select("repo", "main", manifest, [], 10);
        CoverageDebtEntry entry = Assert.Single(selection.Carried);
        Assert.Equal("object-1", entry.GitObjectId);
        Assert.Equal(1, entry.Attempts);

        var reviewed = new ReviewCoverageLedger(ReviewStrategy.Adaptive,
            [new ReviewCoverageEntry(file.Path, file.Kind, true, false, false, ReviewCoverageOutcome.DeepReviewed, null)]);
        Assert.Equal(0, store.Update("repo", "main", manifest, reviewed));
    }

    [Fact]
    public void SelectUsesOldestDebtExcludesCurrentChangesAndDiscardsChangedContent()
    {
        string path = Path.Combine(_directory.Path, "coverage.json");
        DateTimeOffset now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        CoverageDebtEntry[] entries =
        [
            new("repo", "main", "src/old.cs", "old-object", now.AddDays(-3), now.AddDays(-2), "old", 1),
            new("repo", "main", "src/current.cs", "current-object", now.AddDays(-2), now.AddDays(-1), "current", 1),
            new("repo", "main", "src/stale.cs", "obsolete-object", now.AddDays(-4), now.AddDays(-3), "stale", 1),
            new("repo", "main", "src/new.cs", "new-object", now.AddDays(-1), now, "new", 1)
        ];
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
        var store = new CoverageDebtStore(path);
        RepositoryManifest manifest = Manifest(Entry("src/old.cs", "old-object"), Entry("src/current.cs", "current-object"), Entry("src/stale.cs", "replacement-object"),
            Entry("src/new.cs", "new-object"));

        CoverageDebtSelection selection = store.Select("repo", "main", manifest, ["src/current.cs"], 1);

        Assert.Equal(3, selection.PriorCount);
        Assert.Equal(1, selection.DiscardedStaleCount);
        Assert.Equal("src/old.cs", Assert.Single(selection.Carried).Path);
    }

    private static RepositoryManifest Manifest(params RepositoryManifestEntry[] entries) => new("repo", "main", "root", null, "tip", ReviewMode.Changed, "run",
        DateTimeOffset.UtcNow, entries);

    private static RepositoryManifestEntry Entry(string path, string objectId) => new(path, "100644", "blob", objectId, 10, 1, "hash", ".cs", false,
        RepositoryManifestDisposition.Text);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
