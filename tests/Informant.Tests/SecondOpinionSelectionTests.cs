namespace Informant.Tests;

public sealed class SecondOpinionSelectionTests
{
    [Fact]
    public void AllReviewedPreservesBackwardCompatibleScope()
    {
        FileReviewResult[] results = [Candidate("src/b.cs", Severity.High), Clean("src/a.cs"), Skipped("src/c.cs")];
        var config = new SecondOpinionConfig { Scope = SecondOpinionScope.AllReviewed, ReviewSkippedFiles = true };

        IReadOnlyList<SecondOpinionSelectionItem> selected = SecondOpinionSelection.Build(results, config);

        Assert.Equal(["src/b.cs", "src/a.cs", "src/c.cs"], selected.Select(item => item.Result.File.Path));
        Assert.True(selected[0].HasCandidates);
        Assert.True(selected[2].SkippedPrimaryReview);
    }

    [Fact]
    public void CandidateOnlyExcludesCleanResultsAndOrdersBySeverity()
    {
        FileReviewResult[] results = [Candidate("src/medium.cs", Severity.Medium), Clean("src/clean.cs"), Candidate("src/high.cs", Severity.High)];
        var config = new SecondOpinionConfig { Scope = SecondOpinionScope.CandidateOnly, ReviewSkippedFiles = false };

        IReadOnlyList<SecondOpinionSelectionItem> selected = SecondOpinionSelection.Build(results, config);

        Assert.Equal(["src/high.cs", "src/medium.cs"], selected.Select(item => item.Result.File.Path));
        Assert.All(selected, item => Assert.True(item.HasCandidates));
    }

    [Fact]
    public void CandidatePlusSampleAddsBoundedRiskOrderedCleanResults()
    {
        FileReviewResult[] results =
        [
            Clean("src/full.cs", ChangeKind.FullReview),
            Clean("src/added.cs", ChangeKind.Added),
            Candidate("src/candidate.cs", Severity.Low),
            Clean("src/modified.cs", ChangeKind.Modified)
        ];
        var config = new SecondOpinionConfig { Scope = SecondOpinionScope.CandidatePlusSample, MaxCleanFiles = 2, ReviewSkippedFiles = false };

        IReadOnlyList<SecondOpinionSelectionItem> selected = SecondOpinionSelection.Build(results, config);

        Assert.Equal(["src/candidate.cs", "src/modified.cs", "src/added.cs"], selected.Select(item => item.Result.File.Path));
        Assert.True(selected[1].CleanSample);
        Assert.True(selected[2].CleanSample);
    }

    private static FileReviewResult Candidate(string path, Severity severity) => new(new ChangedFile(path, ChangeKind.Modified, [new LineRange(1, 1)]), FileReviewStatus.Reviewed,
        "candidate prose", 1, 1, null, severity, true, CandidateFindings: [new CandidateFinding(path, 1, severity.ToString().ToLowerInvariant(), "candidate")]);

    private static FileReviewResult Clean(string path, ChangeKind kind = ChangeKind.Modified) => new(new ChangedFile(path, kind, [new LineRange(1, 1)]), FileReviewStatus.Reviewed,
        "clean prose", 1, 1, null, Severity.None, true, CandidateFindings: []);

    private static FileReviewResult Skipped(string path) => new(new ChangedFile(path, ChangeKind.Modified, [new LineRange(1, 1)]), FileReviewStatus.Failed, null, 0, 1,
        "primary model failed", Severity.None, false, FileReviewFailureKind.Model);
}
