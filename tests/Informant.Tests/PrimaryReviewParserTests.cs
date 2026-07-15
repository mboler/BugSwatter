namespace Informant.Tests;

public sealed class PrimaryReviewParserTests
{
    [Fact]
    public void ParsesUppercaseJsonFence()
    {
        const string response = """
            Finding.

            ```JSON
            { "findings": [ { "file": "Foo.cs", "line": 7, "severity": "low", "summary": "finding" } ] }
            ```
            """;

        Assert.True(PrimaryReviewParser.TryParse(response, out ParsedPrimaryReview? parsed, out _));
        Assert.Equal(Severity.Low, parsed!.MaxSeverity);
    }

    [Fact]
    public void ParsesCandidateFindingsAndRemovesJsonFromProse()
    {
        const string Text = """
            Definite high-severity issue in Foo.cs.

            ```json
            { "findings": [ { "file": "Foo.cs", "line": 12, "severity": "high", "summary": "unsafe path" } ] }
            ```
            """;

        bool parsed = PrimaryReviewParser.TryParse(Text, out ParsedPrimaryReview? review, out string prose);

        Assert.True(parsed);
        Assert.NotNull(review);
        Assert.Equal(Severity.High, review.MaxSeverity);
        CandidateFinding finding = Assert.Single(review.Findings);
        Assert.Equal("Foo.cs", finding.File);
        Assert.Equal(12, finding.Line);
        Assert.Contains("Definite high-severity issue", prose);
        Assert.DoesNotContain("```json", prose);
    }

    [Fact]
    public void EmptyFindingsAreDeterminedNone()
    {
        const string Text = """
            Nothing of concern.
            ```json
            { "findings": [] }
            ```
            """;

        Assert.True(PrimaryReviewParser.TryParse(Text, out ParsedPrimaryReview? review, out _));
        Assert.Empty(review!.Findings);
        Assert.Equal(Severity.None, review.MaxSeverity);
    }

    [Theory]
    [InlineData("catastrophic")]
    [InlineData("none")]
    [InlineData("")]
    public void UnknownOrNonFindingSeverityFailsSafe(string severity)
    {
        string text = $$"""
            finding
            ```json
            { "findings": [ { "severity": "{{severity}}", "summary": "x" } ] }
            ```
            """;

        Assert.False(PrimaryReviewParser.TryParse(text, out _, out string prose));
        Assert.Equal(text, prose);
    }

    [Fact]
    public void NullFindingElementFailsSafe()
    {
        const string Text = """
            review
            ```json
            { "findings": [null] }
            ```
            """;

        Assert.False(PrimaryReviewParser.TryParse(Text, out _, out string prose));
        Assert.Equal(Text, prose);
    }

    [Fact]
    public void MissingFindingsPropertyFailsSafe()
    {
        const string Text = """
            review
            ```json
            { "verdict": "fine" }
            ```
            """;

        Assert.False(PrimaryReviewParser.TryParse(Text, out _, out _));
    }

    [Fact]
    public void ClassificationUsesHighestSeverityAcrossTheRun()
    {
        FileReviewResult[] results =
        [
            Result("low.cs", Severity.Low),
            Result("high.cs", Severity.High),
            Result("medium.cs", Severity.Medium)
        ];

        PrimaryReviewClassification classification = PrimaryReviewClassification.FromResults(results);

        Assert.True(classification.SeverityDetermined);
        Assert.Equal(Severity.High, classification.MaxSeverity);
        Assert.Equal("high", classification.RouteKey);
    }

    [Fact]
    public void ClassificationIsUndeterminedWhenOneReviewHasMalformedSeverity()
    {
        FileReviewResult[] results =
        [
            Result("high.cs", Severity.High),
            new FileReviewResult(new ChangedFile("unknown.cs", ChangeKind.Modified, [new LineRange(1, 1)]), FileReviewStatus.Reviewed, "prose only", 1, 1, null)
        ];

        PrimaryReviewClassification classification = PrimaryReviewClassification.FromResults(results);

        Assert.False(classification.SeverityDetermined);
        Assert.Equal(Severity.High, classification.MaxSeverity);
        Assert.Equal("undetermined", classification.RouteKey);
    }

    [Theory]
    [InlineData(FileReviewStatus.Failed)]
    [InlineData(FileReviewStatus.Partial)]
    public void ClassificationIsUndeterminedWhenPrimaryReviewDidNotComplete(FileReviewStatus status)
    {
        var result = new FileReviewResult(new ChangedFile("bad.cs", ChangeKind.Modified, [new LineRange(1, 1)]), status, status == FileReviewStatus.Partial ? "partial" : null, 0, 1, "failed");

        Assert.False(PrimaryReviewClassification.FromResults([result]).SeverityDetermined);
    }

    [Fact]
    public void NotReviewableFilesDoNotMakeClassificationUndetermined()
    {
        var result = new FileReviewResult(new ChangedFile("blob.bin", ChangeKind.Modified, []), FileReviewStatus.NotReviewable, null, 0, 0, "binary file");

        PrimaryReviewClassification classification = PrimaryReviewClassification.FromResults([result]);

        Assert.True(classification.SeverityDetermined);
        Assert.Equal(Severity.None, classification.MaxSeverity);
    }

    private static FileReviewResult Result(string path, Severity severity) => new(new ChangedFile(path, ChangeKind.Modified, [new LineRange(1, 1)]), FileReviewStatus.Reviewed, "finding", 1, 1, null, severity, true);
}
