namespace SlimShady.Tests;

public sealed class ReportWriterTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void HeaderCarriesMetadataAndPendingMarkers()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("https://example.test/repo.git", "develop", ReviewMode.Changed, "baseSha", "tipSha", new DateTimeOffset(2026, 7, 10, 14, 30, 0, TimeSpan.FromHours(-5)));
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("# SlimShady Review Report", report);
        Assert.Contains("| Repository | https://example.test/repo.git |", report);
        Assert.Contains("| Branch | develop |", report);
        Assert.Contains("| Baseline SHA | baseSha |", report);
        Assert.Contains("| Reviewed tip SHA | tipSha |", report);
        Assert.Contains("| Review model | test-model |", report);
        Assert.Contains("| Model endpoint | http://localhost:1234/v1 |", report);
        Assert.Contains("| Context budget | 24000 characters |", report);
        Assert.Contains("| Max file lines before chunking | 800 |", report);
        Assert.Contains("(pending: files reviewed)", report);
        Assert.Contains("(pending: run duration)", report);
        Assert.Contains("(pending: run completed)", report);
    }

    [Fact]
    public void NullBaselineRendersFirstRunNote()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, null, "tip", DateTimeOffset.Now);
        Assert.Contains("(none; first run reviews everything)", File.ReadAllText(writer.ReportPath));
    }

    [Fact]
    public void ReviewedSectionAppendsIncrementallyToDisk()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        var file = new ChangedFile("src/Foo.cs", ChangeKind.Modified, [new LineRange(3, 5), new LineRange(9, 9)]);
        writer.AppendFileSection(new FileReviewResult(file, "The findings text from the model.\n", 1, 1, null));

        // On disk immediately, before any finalize, so a crash cannot lose completed sections
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("## src/Foo.cs", report);
        Assert.Contains("Status: Modified | Changed line ranges: 3-5, 9", report);
        Assert.Contains("The findings text from the model.", report);
        Assert.DoesNotContain("SKIPPED", report);
    }

    [Fact]
    public void SkippedSectionNamesTheReason()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("blob.bin", ChangeKind.Modified, []), null, 0, 0, "binary file"));
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("## blob.bin", report);
        Assert.Contains("SKIPPED: binary file", report);
        Assert.Contains("Changed line ranges: (none)", report);
    }

    [Fact]
    public void PartialSectionShowsFindingsAndRemainderNote()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("big.cs", ChangeKind.Modified, [new LineRange(1, 400)]), "part one findings", 1, 3, "part 2 of 3 failed after 2 retries"));
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("Parts reviewed: 1 of 3", report);
        Assert.Contains("part one findings", report);
        Assert.Contains("PARTIAL: remainder skipped, part 2 of 3 failed after 2 retries", report);
    }

    [Fact]
    public void FinalizePatchesHeaderAndAppendsSummary()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("a.cs", ChangeKind.Modified, [new LineRange(1, 2)]), "fine", 1, 1, null));
        writer.Finalize(1, [("blob.bin", "binary file"), ("big.cs", "part 2 of 3 failed")], TimeSpan.FromMinutes(90) + TimeSpan.FromSeconds(5));

        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("| Files reviewed | 1 |", report);
        Assert.Contains("| Files skipped or partial | 2 |", report);
        Assert.Contains("| Run duration | 01:30:05 |", report);
        Assert.DoesNotContain("(pending:", report);
        Assert.Contains("## Run summary", report);
        Assert.Contains("- blob.bin: binary file", report);
        Assert.Contains("- big.cs: part 2 of 3 failed", report);
    }

    [Fact]
    public void FinalizeNeverRewritesFindingsText()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("a.cs", ChangeKind.Modified, [new LineRange(1, 1)]), "the model oddly wrote (pending: files reviewed) in its findings", 1, 1, null));
        writer.Finalize(1, [], TimeSpan.FromSeconds(30));

        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("| Files reviewed | 1 |", report);
        Assert.Contains("the model oddly wrote (pending: files reviewed) in its findings", report);
    }

    [Fact]
    public void DurationBeyondOneDayShowsTotalHours()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.Finalize(0, [], TimeSpan.FromHours(26) + TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(4));
        Assert.Contains("| Run duration | 26:03:04 |", File.ReadAllText(writer.ReportPath));
    }

    private ReportWriter CreateWriter() => new(Path.Combine(_directory.Path, "reports"), "2026-07-10_14-30-00", "test-model", "http://localhost:1234/v1", 24000, 800);
}
