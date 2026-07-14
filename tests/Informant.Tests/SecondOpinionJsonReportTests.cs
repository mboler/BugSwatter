using System.Text.Json;

namespace Informant.Tests;

public sealed class SecondOpinionJsonReportTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void WritesCompanionJsonWithMaxSeverityAndParseFlags()
    {
        var report = new SecondOpinionJsonReport();
        report.Add("Foo.cs", [new LineRange(3, 5)], new ParsedValidation([new ConfirmedFinding("Foo.cs", 4, "high", "bug")], [], "not fit"));
        report.Add("Bar.cs", [new LineRange(1, 2)], null);

        Assert.Equal(Severity.High, report.MaxSeverity);
        Assert.False(report.SeverityDetermined);

        var selection = new SecondOpinionModelSelection("premium", new SecondOpinionModelProfile { ModelName = "gpt-x", Endpoint = "https://api.example/v1" },
            new PrimaryReviewClassification(Severity.High, true), true);
        string path = report.Write(_directory.Path, "2026-07-11_10-00-00", selection, "Informant-Report-2026-07-11_10-00-00.md");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("premium", document.RootElement.GetProperty("modelProfile").GetString());
        Assert.Equal("High", document.RootElement.GetProperty("primaryCandidateSeverity").GetString());
        Assert.True(document.RootElement.GetProperty("primarySeverityDetermined").GetBoolean());
        Assert.Equal("Undetermined", document.RootElement.GetProperty("maxSeverity").GetString());
        Assert.False(document.RootElement.GetProperty("severityDetermined").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("fileCount").GetInt32());
        JsonElement foo = document.RootElement.GetProperty("files")[0];
        Assert.True(foo.GetProperty("parseOk").GetBoolean());
        Assert.Equal("high", foo.GetProperty("confirmed")[0].GetProperty("severity").GetString());
        JsonElement bar = document.RootElement.GetProperty("files")[1];
        Assert.False(bar.GetProperty("parseOk").GetBoolean());
    }

    [Fact]
    public void NoConfirmedFindingsYieldsNoneSeverity()
    {
        var report = new SecondOpinionJsonReport();
        report.Add("Clean.cs", [], new ParsedValidation([], [], "fine to ship"));
        Assert.Equal(Severity.None, report.MaxSeverity);
        Assert.True(report.SeverityDetermined);
    }
}
