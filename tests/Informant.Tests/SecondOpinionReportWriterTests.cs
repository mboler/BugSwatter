namespace Informant.Tests;

public sealed class SecondOpinionReportWriterTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void HeaderRecordsWhyTheModelWasSelected()
    {
        var selection = new SecondOpinionModelSelection("premium", new SecondOpinionModelProfile { ModelName = "gpt-x", Endpoint = "https://api.example/v1" },
            new PrimaryReviewClassification(Severity.High, true), true);
        var writer = new SecondOpinionReportWriter(_directory.Path, "2026-07-13_10-00-00");

        writer.WriteHeader(selection, "Informant-Report-2026-07-13_10-00-00.md", new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.FromHours(-5)), 30);

        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("| Validating model | gpt-x |", report);
        Assert.Contains("| Model profile | premium |", report);
        Assert.Contains("| Primary candidate severity | High |", report);
        Assert.Contains("profile 'premium' selected because the highest primary candidate severity was High", report);
    }

    [Fact]
    public void FailureSectionRecordsCauseAndPreservesResponseWhenAvailable()
    {
        var selection = new SecondOpinionModelSelection("premium", new SecondOpinionModelProfile { ModelName = "gpt-x", Endpoint = "https://api.example/v1" },
            new PrimaryReviewClassification(Severity.High, true), true);
        var writer = new SecondOpinionReportWriter(_directory.Path, "2026-07-13_10-00-00");
        writer.WriteHeader(selection, "source.md", DateTimeOffset.UnixEpoch, 30);

        writer.AppendFailureSection("Request.cs", [], SecondOpinionValidationStatus.RequestFailed, "Model endpoint returned 500");
        writer.AppendFailureSection("Parse.cs", [new LineRange(4, 4)], SecondOpinionValidationStatus.ParseFailed, "Structured findings were not found", "free-form answer");

        string report = File.ReadAllText(writer.ReportPath);
        Assert.Contains("VALIDATION INCOMPLETE (RequestFailed): Model endpoint returned 500", report);
        Assert.Contains("VALIDATION INCOMPLETE (ParseFailed): Structured findings were not found", report);
        Assert.Contains("Model response:", report);
        Assert.Contains("free-form answer", report);
        Assert.DoesNotContain("did not return parseable structured findings", report);
    }
}
