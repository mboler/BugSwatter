using System.Text.Json;

namespace Informant.Tests;

public sealed class SecondOpinionJsonReportTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void WritesConfirmedSeverityAndExplicitFailureStatus()
    {
        var report = new SecondOpinionJsonReport();
        report.AddValidated("Foo.cs", [new LineRange(3, 5)], new ParsedValidation([new ConfirmedFinding("Foo.cs", 4, "high", "bug")], [], "not fit"));
        report.AddFailure("Bar.cs", [new LineRange(1, 2)], SecondOpinionValidationStatus.RequestFailed, "Model endpoint returned 500");

        Assert.Equal(Severity.High, report.MaxSeverity);
        Assert.False(report.SeverityDetermined);
        Assert.Equal(1, report.RequestFailureCount);

        var selection = new SecondOpinionModelSelection("premium", new SecondOpinionModelProfile { ModelName = "gpt-x", Endpoint = "https://api.example/v1" },
            new PrimaryReviewClassification(Severity.High, true), true);
        string path = report.Write(_directory.Path, "2026-07-11_10-00-00", selection, "Informant-Report-2026-07-11_10-00-00.md");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("premium", document.RootElement.GetProperty("modelProfile").GetString());
        Assert.Equal("High", document.RootElement.GetProperty("primaryCandidateSeverity").GetString());
        Assert.True(document.RootElement.GetProperty("primarySeverityDetermined").GetBoolean());
        Assert.Equal("High", document.RootElement.GetProperty("maxSeverity").GetString());
        Assert.False(document.RootElement.GetProperty("severityDetermined").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("validatedCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("requestFailureCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("parseFailureCount").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("fileCount").GetInt32());
        JsonElement foo = document.RootElement.GetProperty("files")[0];
        Assert.Equal("Validated", foo.GetProperty("status").GetString());
        Assert.True(foo.GetProperty("parseOk").GetBoolean());
        Assert.Equal("high", foo.GetProperty("confirmed")[0].GetProperty("severity").GetString());
        JsonElement bar = document.RootElement.GetProperty("files")[1];
        Assert.Equal("RequestFailed", bar.GetProperty("status").GetString());
        Assert.False(bar.GetProperty("parseOk").GetBoolean());
        Assert.Equal("Model endpoint returned 500", bar.GetProperty("failureReason").GetString());
    }

    [Fact]
    public void NoConfirmedFindingsYieldsNoneSeverity()
    {
        var report = new SecondOpinionJsonReport();
        report.AddValidated("Clean.cs", [], new ParsedValidation([], [], "fine to ship"));
        Assert.Equal(Severity.None, report.MaxSeverity);
        Assert.True(report.SeverityDetermined);
    }

    [Fact]
    public void FailureCountsRemainDistinct()
    {
        var report = new SecondOpinionJsonReport();
        report.AddFailure("Request.cs", [], SecondOpinionValidationStatus.RequestFailed, "HTTP 500");
        report.AddFailure("Empty.cs", [], SecondOpinionValidationStatus.EmptyResponse, "empty");
        report.AddFailure("Parse.cs", [], SecondOpinionValidationStatus.ParseFailed, "invalid structure");

        Assert.Equal(1, report.RequestFailureCount);
        Assert.Equal(1, report.EmptyResponseCount);
        Assert.Equal(1, report.ParseFailureCount);
        Assert.False(report.SeverityDetermined);
    }
}
