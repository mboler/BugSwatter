using System.Text.Json;

namespace Informant.Tests;

/// <summary>Tests adaptive planning augmentation, honest coverage labels, artifacts, and baseline rules</summary>
public sealed class ReviewCoverageLedgerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    /// <summary>Verifies adaptive deferrals of modified files receive mandatory changed-content units</summary>
    [Fact]
    public void AdaptivePlanAddsMandatoryChangedContentForDeferredChanges()
    {
        ChangedFile[] files =
        [
            new ChangedFile("src/Changed.cs", ChangeKind.Modified, [new LineRange(10, 12)]),
            new ChangedFile("src/FirstRun.cs", ChangeKind.FullReview, [])
        ];
        RepositoryReviewPlan plan = Plan([], [new RepositoryReviewDeferral(files[0].Path, "defer"), new RepositoryReviewDeferral(files[1].Path, "defer")]);

        RepositoryReviewPlan augmented = RepositoryAdaptivePlan.AddMandatoryChangedContent(plan, files, ReviewStrategy.Adaptive);

        RepositoryReviewUnit unit = Assert.Single(augmented.Units);
        Assert.True(unit.ChangedLinesOnly);
        Assert.Equal([files[0].Path], unit.Paths);
        Assert.Equal(2, augmented.Deferred.Count);
    }

    /// <summary>Verifies a completed mandatory changed-content review permits adaptive baseline advancement without claiming deep review</summary>
    [Fact]
    public void CompletedMandatoryChangesPermitAdaptiveBaselineAdvancement()
    {
        var file = new ChangedFile("src/Changed.cs", ChangeKind.Modified, [new LineRange(10, 12)]);
        RepositoryReviewPlan plan = Plan([new RepositoryReviewUnit("mandatory", 95, "changed", [file.Path], [], ChangedLinesOnly: true)],
            [new RepositoryReviewDeferral(file.Path, "lower priority")]);
        FileReviewResult[] results = [new(file, FileReviewStatus.Deferred, "reviewed changed lines", 1, 1, "deep review deferred", Severity.None, true)];

        ReviewCoverageLedger ledger = ReviewCoverageLedger.Create(ReviewStrategy.Adaptive, [file], plan, results);

        ReviewCoverageEntry entry = Assert.Single(ledger.Entries);
        Assert.Equal(ReviewCoverageOutcome.MandatoryChangesReviewed, entry.Outcome);
        Assert.True(entry.DeepReviewDeferred);
        Assert.False(entry.SelectedForDeepReview);
        Assert.True(ReviewCompletion.CanAdvanceBaseline(ledger));
    }

    /// <summary>Verifies a first-run path may be honestly deferred without blocking the approved adaptive baseline rule</summary>
    [Fact]
    public void FullReviewDeferralIsRecordedAndPermitsAdaptiveBaselineAdvancement()
    {
        var file = new ChangedFile("src/FirstRun.cs", ChangeKind.FullReview, []);
        RepositoryReviewPlan plan = Plan([], [new RepositoryReviewDeferral(file.Path, "not selected")]);
        FileReviewResult[] results = [new(file, FileReviewStatus.Deferred, null, 0, 0, "deep review deferred")];

        ReviewCoverageLedger ledger = ReviewCoverageLedger.Create(ReviewStrategy.Adaptive, [file], plan, results);

        ReviewCoverageEntry entry = Assert.Single(ledger.Entries);
        Assert.Equal(ReviewCoverageOutcome.Deferred, entry.Outcome);
        Assert.False(entry.MandatoryChangedContent);
        Assert.True(ledger.CanAdvanceBaseline);
    }

    /// <summary>Verifies failed mandatory changed-content work preserves the previous baseline</summary>
    [Fact]
    public void FailedMandatoryChangesPreventAdaptiveBaselineAdvancement()
    {
        var file = new ChangedFile("src/Changed.cs", ChangeKind.Modified, [new LineRange(10, 12)]);
        RepositoryReviewPlan plan = Plan([new RepositoryReviewUnit("mandatory", 95, "changed", [file.Path], [], ChangedLinesOnly: true)],
            [new RepositoryReviewDeferral(file.Path, "lower priority")]);
        FileReviewResult[] results = [new(file, FileReviewStatus.Failed, null, 0, 1, "model failed", FailureKind: FileReviewFailureKind.Model)];

        ReviewCoverageLedger ledger = ReviewCoverageLedger.Create(ReviewStrategy.Adaptive, [file], plan, results);

        Assert.Equal(ReviewCoverageOutcome.Failed, Assert.Single(ledger.Entries).Outcome);
        Assert.False(ledger.CanAdvanceBaseline);
    }

    /// <summary>Verifies the coverage JSON artifact contains metadata but not model findings or source bodies</summary>
    [Fact]
    public void CoverageArtifactContainsNoFindingsOrSourceContent()
    {
        var file = new ChangedFile("src/Changed.cs", ChangeKind.Modified, [new LineRange(1, 1)]);
        RepositoryReviewPlan plan = Plan([new RepositoryReviewUnit("deep", 1, "selected", [file.Path], [])], []);
        FileReviewResult[] results = [new(file, FileReviewStatus.Reviewed, "SECRET MODEL FINDING", 1, 1, null, Severity.Low, true)];
        ReviewCoverageLedger ledger = ReviewCoverageLedger.Create(ReviewStrategy.Exhaustive, [file], plan, results);

        string path = ReviewCoverageLedgerFile.Write(_directory.Path, "2026-07-14_12-00-00", ledger);
        string json = File.ReadAllText(path);

        Assert.DoesNotContain("SECRET MODEL FINDING", json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("DeepReviewed", document.RootElement.GetProperty("entries")[0].GetProperty("outcome").GetString());
    }

    private static RepositoryReviewPlan Plan(IReadOnlyList<RepositoryReviewUnit> units, IReadOnlyList<RepositoryReviewDeferral> deferred) =>
        new("repository", units, deferred, [], false, false, []);
}
