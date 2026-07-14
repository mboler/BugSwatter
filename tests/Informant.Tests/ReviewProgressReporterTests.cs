namespace Informant.Tests;

public sealed class ReviewProgressReporterTests
{
    [Fact]
    public void DisabledReporterWritesNothing()
    {
        var output = new StringWriter();
        var reporter = new ReviewProgressReporter(ProgressOutput.None, output);

        reporter.ReportPhase("Preparing repository");
        reporter.ObserveModelCall(new ModelCallProgress(ModelCallState.Started, "model", DateTimeOffset.UtcNow, TimeSpan.Zero, null));
        reporter.ReportCompleted();

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void JsonReporterEmitsCompleteSnapshotsAndAggregatesProviderUsage()
    {
        var output = new StringWriter();
        var reporter = new ReviewProgressReporter(ProgressOutput.Json, output);
        DateTimeOffset firstStartedUtc = DateTimeOffset.Parse("2026-07-13T08:00:00Z");
        DateTimeOffset secondStartedUtc = firstStartedUtc.AddMinutes(1);

        reporter.ReportPhase("Verifying primary model", "local-model", "primary");
        reporter.ObserveModelCall(new ModelCallProgress(ModelCallState.Started, "local-model", firstStartedUtc, TimeSpan.Zero, null));
        reporter.ObserveModelCall(new ModelCallProgress(ModelCallState.Completed, "local-model", firstStartedUtc, TimeSpan.FromSeconds(2), new ModelTokenUsage(100, 20, 120)));
        reporter.ReportFile("Primary review", "local-model", "primary", "src/Worker.cs", 2, 5);
        reporter.ObserveModelCall(new ModelCallProgress(ModelCallState.Started, "local-model", secondStartedUtc, TimeSpan.Zero, null));
        reporter.ObserveModelCall(new ModelCallProgress(ModelCallState.Completed, "local-model", secondStartedUtc, TimeSpan.FromSeconds(3), new ModelTokenUsage(200, 40, 240)));

        ReviewProgressSnapshot[] snapshots = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(Parse)
            .ToArray();

        Assert.Equal(6, snapshots.Length);
        Assert.True(snapshots[1].ModelRequestActive);
        Assert.Equal(firstStartedUtc, snapshots[1].ModelRequestStartedUtc);
        Assert.Equal(1, snapshots[1].ModelRequestCount);
        Assert.False(snapshots[^1].ModelRequestActive);
        Assert.Null(snapshots[^1].ModelRequestStartedUtc);
        Assert.Equal(2, snapshots[^1].ModelRequestCount);
        Assert.Equal(300, snapshots[^1].PromptTokens);
        Assert.Equal(60, snapshots[^1].CompletionTokens);
        Assert.Equal(360, snapshots[^1].TotalTokens);
        Assert.Equal("src/Worker.cs", snapshots[^1].CurrentFile);
        Assert.Equal(2, snapshots[^1].FileIndex);
        Assert.Equal(5, snapshots[^1].FileCount);
    }

    [Fact]
    public void ModelFailoverUpdatesTargetWithoutLosingCurrentFile()
    {
        var output = new StringWriter();
        var reporter = new ReviewProgressReporter(ProgressOutput.Json, output);
        reporter.ReportFile("Primary review", "primary-model", "primary", "src/Worker.cs", 2, 5);

        reporter.ReportModelTarget("backup-model", "backup-server");

        ReviewProgressSnapshot snapshot = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(Parse)
            .Last();
        Assert.Equal("backup-model", snapshot.ModelName);
        Assert.Equal("backup-server", snapshot.ModelProfile);
        Assert.Equal("src/Worker.cs", snapshot.CurrentFile);
        Assert.Equal(2, snapshot.FileIndex);
        Assert.Equal(5, snapshot.FileCount);
    }

    private static ReviewProgressSnapshot Parse(string line)
    {
        Assert.True(ReviewProgressMarker.TryParse(line, out ReviewProgressSnapshot? snapshot));
        return Assert.IsType<ReviewProgressSnapshot>(snapshot);
    }
}
