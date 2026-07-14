namespace Informant.Tests;

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
        Assert.Contains("# Informant Review Report", report);
        Assert.Contains("| Repository | https://example.test/repo.git |", report);
        Assert.Contains("| Branch | develop |", report);
        Assert.Contains("| Baseline SHA | baseSha |", report);
        Assert.Contains("| Reviewed tip SHA | tipSha |", report);
        Assert.Contains("| Review model | test-model |", report);
        Assert.Contains("| Model endpoint | http://localhost:1234/v1 |", report);
        Assert.Contains("| Configured fallback models | 0 |", report);
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
        writer.AppendFileSection(new FileReviewResult(file, FileReviewStatus.Reviewed, "The findings text from the model.\n", 1, 1, null));

        // On disk immediately, before any finalize, so a crash cannot lose completed sections
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("## src/Foo.cs", report);
        Assert.Contains("Status: Modified | Changed line ranges: 3-5, 9", report);
        Assert.Contains("The findings text from the model.", report);
        Assert.DoesNotContain("SKIPPED", report);
    }

    [Fact]
    public void ReviewedSectionRecordsTheModelThatProducedIt()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        var file = new ChangedFile("src/Foo.cs", ChangeKind.Modified, [new LineRange(1, 1)]);
        writer.AppendFileSection(new FileReviewResult(file, FileReviewStatus.Reviewed, "findings", 1, 1, null, ReviewModelName: "backup-model", ReviewModelProfile: "backup-server"));

        string report = File.ReadAllText(writer.ReportPath);

        Assert.Contains("Review model: backup-server (`backup-model`)", report);
    }

    [Fact]
    public void NotReviewableSectionNamesTheReason()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("blob.bin", ChangeKind.Modified, []), FileReviewStatus.NotReviewable, null, 0, 0, "binary file"));
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("## blob.bin", report);
        Assert.Contains("Review result: NotReviewable", report);
        Assert.Contains("NOT REVIEWABLE: binary file", report);
        Assert.Contains("Changed line ranges: (none)", report);
    }

    [Fact]
    public void PartialSectionShowsFindingsAndRemainderNote()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("big.cs", ChangeKind.Modified, [new LineRange(1, 400)]), FileReviewStatus.Partial, "part one findings", 1, 3,
            "part 2 of 3 failed after 2 retries"));
        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("Parts reviewed: 1 of 3", report);
        Assert.Contains("part one findings", report);
        Assert.Contains("Review result: Partial", report);
        Assert.Contains("PARTIAL: remainder not reviewed, part 2 of 3 failed after 2 retries", report);
    }

    [Fact]
    public void FinalizePatchesHeaderAndAppendsSummary()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("a.cs", ChangeKind.Modified, [new LineRange(1, 2)]), FileReviewStatus.Reviewed, "fine", 1, 1, null));
        writer.Finalize(1, [("blob.bin", "binary file"), ("big.cs", "part 2 of 3 failed")], TimeSpan.FromMinutes(90) + TimeSpan.FromSeconds(5));

        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("| Files reviewed | 1 |", report);
        Assert.Contains("| Files skipped or partial | 2 |", report);
        Assert.Contains("| Run duration | 01:30:05 |", report);
        Assert.DoesNotContain("(pending:", report);
        Assert.Contains("## Run summary", report);
        Assert.Contains("- blob.bin: binary file", report);
        Assert.Contains("- big.cs: part 2 of 3 failed", report);
        Assert.Contains("Primary model target failures: 0", report);
    }

    [Fact]
    public void FinalizeRecordsFailoverReasonInTheSingleReport()
    {
        var targets = new[]
        {
            new PrimaryModelTarget("primary", "http://primary.example/v1", "primary-model", false),
            new PrimaryModelTarget("backup", "http://backup.example/v1", "backup-model", true)
        };
        var writer = new ReportWriter(Path.Combine(_directory.Path, "reports"), "2026-07-10_14-30-00", "primary-model", "http://primary.example/v1", 24000, 800, targets);
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.Finalize(1, [], TimeSpan.FromMinutes(2), [new PrimaryModelFailure(targets[0], "requests timed out")]);

        string report = File.ReadAllText(writer.ReportPath);

        Assert.Contains("| Configured fallback models | 1 |", report);
        Assert.Contains("Primary model target failures: 1", report);
        Assert.Contains("- primary (`primary-model`): requests timed out", report);
        Assert.Equal(1, CountOccurrences(report, "# Informant Review Report"));
    }

    [Fact]
    public void FinalizeNeverRewritesFindingsText()
    {
        ReportWriter writer = CreateWriter();
        writer.WriteHeader("repo", "main", ReviewMode.Changed, "base", "tip", DateTimeOffset.Now);
        writer.AppendFileSection(new FileReviewResult(new ChangedFile("a.cs", ChangeKind.Modified, [new LineRange(1, 1)]), FileReviewStatus.Reviewed,
            "the model oddly wrote (pending: files reviewed) in its findings", 1, 1, null));
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

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
