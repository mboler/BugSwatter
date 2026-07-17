using BugSwatter.Common;

namespace Marshal.Tests;

public sealed class RunHistoryStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AppendsAndReadsBackNewestFirst()
    {
        var store = new RunHistoryStore(Path.Combine(_directory.Path, "history.jsonl"));
        store.Append(new HistoryEntry { Job = "first", Outcome = "completed" });
        store.Append(new HistoryEntry { Job = "second", Outcome = "failed", ExitCode = 1, RunUsage = new ReviewUsageSnapshot { RequestCount = 4, TotalTokens = 500 },
            FrontierUsage = new ReviewUsageSnapshot { RequestCount = 2, TotalTokens = 200, EstimatedCost = 1.25m } });

        IReadOnlyList<HistoryEntry> recent = store.ReadRecent(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("second", recent[0].Job);
        Assert.Equal("first", recent[1].Job);
        Assert.Equal(1, recent[0].ExitCode);
        Assert.Equal(4, recent[0].RunUsage!.RequestCount);
        Assert.Equal(500, recent[0].RunUsage!.TotalTokens);
        Assert.Equal(1.25m, recent[0].FrontierUsage!.EstimatedCost);
    }

    [Fact]
    public void ReadRecentRespectsTheLimit()
    {
        var store = new RunHistoryStore(Path.Combine(_directory.Path, "history.jsonl"));
        for (int index = 0; index < 5; index++)
        {
            store.Append(new HistoryEntry { Job = $"job{index}", Outcome = "completed" });
        }

        IReadOnlyList<HistoryEntry> recent = store.ReadRecent(2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("job4", recent[0].Job);
        Assert.Equal("job3", recent[1].Job);
    }

    [Fact]
    public void MissingFileReadsEmpty()
    {
        var store = new RunHistoryStore(Path.Combine(_directory.Path, "never-written.jsonl"));
        Assert.Empty(store.ReadRecent(10));
    }

    [Fact]
    public void CorruptLineIsSkippedNotFatal()
    {
        string path = Path.Combine(_directory.Path, "history.jsonl");
        var store = new RunHistoryStore(path);
        store.Append(new HistoryEntry { Job = "good", Outcome = "completed" });
        File.AppendAllText(path, "this is not json\n");
        store.Append(new HistoryEntry { Job = "alsoGood", Outcome = "completed" });

        IReadOnlyList<HistoryEntry> recent = store.ReadRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("alsoGood", recent[0].Job);
        Assert.Equal("good", recent[1].Job);
    }

    [Fact]
    public void MaxSeverityIsReadFromTheValidatedCompanion()
    {
        string reportPath = Path.Combine(_directory.Path, "Informant-Report-2026-07-11_10-00-00.md");
        File.WriteAllText(reportPath, "report");
        File.WriteAllText(Path.Combine(_directory.Path, "Informant-Report-2026-07-11_10-00-00-validated.json"), """{ "maxSeverity": "High" }""");

        Assert.Equal("High", RunHistoryStore.TryReadMaxSeverity(reportPath));
    }

    [Fact]
    public void MaxSeverityIsReadWhenTheDiscoveredPathIsTheValidatedReport()
    {
        // After a second opinion runs, the newest report Marshal discovers is the validated report itself,
        // so the discovered path ends with -validated.md; the companion json must still be found
        string validatedReportPath = Path.Combine(_directory.Path, "Informant-Report-2026-07-11_10-00-00-validated.md");
        File.WriteAllText(validatedReportPath, "validated report");
        File.WriteAllText(Path.Combine(_directory.Path, "Informant-Report-2026-07-11_10-00-00-validated.json"), """{ "maxSeverity": "High" }""");

        Assert.Equal("High", RunHistoryStore.TryReadMaxSeverity(validatedReportPath));
    }

    [Fact]
    public void MaxSeverityIsNullWhenNoCompanion()
    {
        Assert.Null(RunHistoryStore.TryReadMaxSeverity(Path.Combine(_directory.Path, "Informant-Report-x.md")));
        Assert.Null(RunHistoryStore.TryReadMaxSeverity(null));
    }
}
