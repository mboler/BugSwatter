namespace Informant.Tests;

/// <summary>Persistent finding acceptance and suppression-state regression coverage</summary>
public sealed class FindingStateStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    /// <summary>Verifies only new findings enter the accepted repository and branch ledger</summary>
    [Fact]
    public void AcceptNewPersistsOnlyNewFindings()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        var store = new FindingStateStore(StatePath(), timeProvider);
        FindingRunArtifact artifact = Artifact(
            Entry("new", FindingDisposition.New),
            Entry("known", FindingDisposition.Known),
            Entry("suppressed", FindingDisposition.Suppressed));

        Assert.Equal(1, store.AcceptNew(artifact));
        Assert.Equal(0, store.AcceptNew(artifact));

        FindingStateSnapshot snapshot = new FindingStateStore(StatePath()).LoadSnapshot("repository", "main");
        AcceptedFindingState accepted = Assert.Single(snapshot.Accepted);
        Assert.Equal("new", accepted.Fingerprint);
        Assert.Equal(timeProvider.GetUtcNow(), accepted.AcceptedAtUtc);
        Assert.Empty(new FindingStateStore(StatePath()).LoadSnapshot("repository", "develop").Accepted);
    }

    /// <summary>Verifies suppression age retains its first observation while its latest observation advances</summary>
    [Fact]
    public void ObserveSuppressionsRetainsFirstObservation()
    {
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        var store = new FindingStateStore(StatePath(), timeProvider);
        FindingArtifactEntry finding = Entry("suppressed", FindingDisposition.Suppressed) with { SuppressionJustification = "accepted operational risk", SuppressionMarkerLine = 4 };

        store.ObserveSuppressions("repository", "main", [finding]);
        DateTimeOffset first = timeProvider.GetUtcNow();
        timeProvider.Advance(TimeSpan.FromDays(100));
        FindingStateSnapshot snapshot = store.ObserveSuppressions("repository", "main", [finding]);

        FindingSuppressionState suppression = Assert.Single(snapshot.Suppressions);
        Assert.Equal(first, suppression.FirstObservedUtc);
        Assert.Equal(timeProvider.GetUtcNow(), suppression.LastObservedUtc);
        Assert.Equal("accepted operational risk", suppression.Justification);
        Assert.Equal(4, suppression.MarkerLine);
    }

    /// <summary>Verifies managed artifacts round-trip and latest selection ignores unrelated names</summary>
    [Fact]
    public void ArtifactFileLoadsLatestMatchingArtifact()
    {
        FindingArtifactFile.Write(_directory.Path, "2026-08-15_10-00-00", Artifact(Entry("older", FindingDisposition.New)));
        FindingArtifactFile.Write(_directory.Path, "2026-08-15_11-00-00", Artifact(Entry("newer", FindingDisposition.New)));
        File.WriteAllText(Path.Combine(_directory.Path, "Informant-Findings-latest.json"), "{}");

        (string path, FindingRunArtifact artifact) = FindingArtifactFile.LoadLatest(_directory.Path, "repository", "main");

        Assert.EndsWith("Informant-Findings-2026-08-15_11-00-00.json", path, StringComparison.Ordinal);
        Assert.Equal("newer", Assert.Single(artifact.Findings).Fingerprint);
    }

    /// <summary>Verifies unsupported artifact versions are rejected before acceptance</summary>
    [Fact]
    public void AcceptNewRejectsUnsupportedArtifactVersion()
    {
        var store = new FindingStateStore(StatePath());
        FindingRunArtifact artifact = Artifact(Entry("new", FindingDisposition.New)) with { Version = 99 };

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => store.AcceptNew(artifact));

        Assert.Contains("version 99", exception.Message);
        Assert.False(File.Exists(StatePath()));
    }

    private string StatePath() => Path.Combine(_directory.Path, "findings.json");

    private static FindingRunArtifact Artifact(params FindingArtifactEntry[] findings) => new(1, "repository", "main", "tip", DateTimeOffset.UnixEpoch, findings, 0, 0, 0);

    private static FindingArtifactEntry Entry(string fingerprint, FindingDisposition disposition) => new(fingerprint, 1, FindingOrigin.Primary, "src/Example.cs", "src/Example.cs", 3, 3,
        "correctness", "high", "example finding", "{", "return value;", "}", disposition, FindingValidatorStatus.NotRun);

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
