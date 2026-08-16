namespace Informant.Tests;

/// <summary>Finding disposition integration coverage across primary and second-opinion results</summary>
public sealed class FindingRunTrackerTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    /// <summary>Verifies a justified inline suppression removes its candidate from severity routing</summary>
    [Fact]
    public async Task SuppressedCandidateIsFilteredAndRecorded()
    {
        WriteSource("// bugswatter-ignore: required compatibility behavior", "", "DangerousCall();");
        FileReviewResult result = Result(new CandidateFinding("src/Example.cs", 3, "high", "unsafe call", "security"));
        FindingRunTracker tracker = await CreateTrackerAsync([result]);

        FileReviewResult filtered = Assert.Single(tracker.FilterSuppressedCandidates([result]));
        FindingRunSummary summary = tracker.WriteArtifact(_directory.Path, "2026-08-15_12-00-00");
        FindingRunArtifact artifact = FindingArtifactFile.LoadLatest(_directory.Path, "repository", "main").Artifact;

        Assert.Empty(filtered.StructuredCandidates);
        Assert.Equal(Severity.None, filtered.CandidateSeverity);
        Assert.Equal(1, summary.SuppressedCount);
        FindingArtifactEntry finding = Assert.Single(artifact.Findings);
        Assert.Equal(FindingDisposition.Suppressed, finding.Disposition);
        Assert.Equal("required compatibility behavior", finding.SuppressionJustification);
    }

    /// <summary>Verifies exact structural second-opinion findings reconcile primary validator status</summary>
    [Fact]
    public async Task SecondOpinionReconcilesConfirmedAndDiscardedFindings()
    {
        WriteSource("class Example", "{", "    DangerousCall();", "    OtherCall();", "}");
        CandidateFinding confirmedCandidate = new("src/Example.cs", 3, "high", "unsafe call", "security");
        CandidateFinding discardedCandidate = new("src/Example.cs", 4, "medium", "unnecessary call", "correctness");
        FileReviewResult result = Result(confirmedCandidate, discardedCandidate);
        FindingRunTracker tracker = await CreateTrackerAsync([result]);
        var validation = new SecondOpinionFileValidation("src/Example.cs", "3-4", SecondOpinionValidationStatus.Validated, true,
            [new ConfirmedFinding("src/Example.cs", 3, "high", "confirmed", "security")],
            [new DiscardedFinding("discarded", "not reachable", "src/Example.cs", 4, "correctness")], "one confirmed", null);

        await tracker.ApplySecondOpinionAsync(true, [validation]);
        tracker.WriteArtifact(_directory.Path, "2026-08-15_12-00-00");
        FindingRunArtifact artifact = FindingArtifactFile.LoadLatest(_directory.Path, "repository", "main").Artifact;

        Assert.Contains(artifact.Findings, finding => finding.Origin == FindingOrigin.Primary && finding.ValidatorStatus == FindingValidatorStatus.Confirmed && finding.AnchorLine == 3);
        Assert.Contains(artifact.Findings, finding => finding.Origin == FindingOrigin.Primary && finding.ValidatorStatus == FindingValidatorStatus.Discarded && finding.AnchorLine == 4);
    }

    /// <summary>Verifies an accepted structural finding becomes known on a later run</summary>
    [Fact]
    public async Task AcceptedFindingBecomesKnownOnNextRun()
    {
        WriteSource("class Example", "{", "    DangerousCall();", "}");
        FileReviewResult result = Result(new CandidateFinding("src/Example.cs", 3, "high", "unsafe call", "security"));
        FindingRunTracker first = await CreateTrackerAsync([result]);
        first.WriteArtifact(_directory.Path, "2026-08-15_12-00-00");
        FindingRunArtifact firstArtifact = FindingArtifactFile.LoadLatest(_directory.Path, "repository", "main").Artifact;
        Assert.Equal(1, new FindingStateStore(StatePath()).AcceptNew(firstArtifact));

        FindingRunTracker second = await CreateTrackerAsync([result]);
        FindingRunSummary summary = second.WriteArtifact(_directory.Path, "2026-08-15_13-00-00");

        Assert.Equal(0, summary.NewCount);
        Assert.Equal(1, summary.KnownCount);
    }

    private async Task<FindingRunTracker> CreateTrackerAsync(IReadOnlyList<FileReviewResult> results)
    {
        RepositoryManifest manifest = CreateManifest();
        return await FindingRunTracker.CreateAsync("repository", "main", "tip", results, manifest, _directory.Path, 1024 * 1024, new GitRunner("git"), StatePath());
    }

    private RepositoryManifest CreateManifest()
    {
        var reader = new RepositoryFileReader(_directory.Path);
        RepositoryFileInspection inspection = reader.Inspect("src/Example.cs");
        var entry = new RepositoryManifestEntry("src/Example.cs", "100644", "blob", "object", inspection.SizeBytes, inspection.LineCount, inspection.ContentHash, ".cs", false,
            RepositoryManifestDisposition.Text, ChangeKind.Modified);
        return new RepositoryManifest("repository", "main", reader.Root, null, "tip", ReviewMode.Changed, "run", DateTimeOffset.UnixEpoch, [entry]);
    }

    private static FileReviewResult Result(params CandidateFinding[] candidates)
    {
        Severity severity = candidates.Select(candidate => SecondOpinionParser.ParseSeverity(candidate.Severity)).Max();
        return new FileReviewResult(new ChangedFile("src/Example.cs", ChangeKind.Modified, [new LineRange(1, 20)]), FileReviewStatus.Reviewed, "findings", 1, 1, null, severity, true,
            CandidateFindings: candidates);
    }

    private string StatePath() => Path.Combine(_directory.Path, "finding-state.json");

    private void WriteSource(params string[] lines)
    {
        string path = Path.Combine(_directory.Path, "src", "Example.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }
}
