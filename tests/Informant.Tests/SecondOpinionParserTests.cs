namespace Informant.Tests;

public sealed class SecondOpinionParserTests
{
    [Fact]
    public void ParsesFencedJsonAndStripsItFromProse()
    {
        const string Text = """
            CONFIRMED FINDINGS
            1. Foo.cs line 12, high: SQL injection.

            VERDICT
            Not fit to ship.

            ```json
            { "confirmed": [ { "file": "Foo.cs", "line": 12, "severity": "high", "summary": "SQL injection" } ], "discarded": [], "verdict": "Not fit to ship." }
            ```
            """;

        bool ok = SecondOpinionParser.TryParse(Text, out ParsedValidation? parsed, out string prose);

        Assert.True(ok);
        Assert.NotNull(parsed);
        ConfirmedFinding finding = Assert.Single(parsed.Confirmed);
        Assert.Equal("Foo.cs", finding.File);
        Assert.Equal(12, finding.Line);
        Assert.Equal("high", finding.Severity);
        Assert.Contains("CONFIRMED FINDINGS", prose);
        Assert.DoesNotContain("```json", prose);
    }

    [Fact]
    public void UntaggedFenceStillParses()
    {
        const string Text = """
            VERDICT ok
            ```
            { "confirmed": [], "discarded": [], "verdict": "fine" }
            ```
            """;

        Assert.True(SecondOpinionParser.TryParse(Text, out ParsedValidation? parsed, out _));
        Assert.Empty(parsed!.Confirmed);
        Assert.Equal("fine", parsed.Verdict);
    }

    [Fact]
    public void MalformedJsonLeavesProseUntouchedAndReturnsFalse()
    {
        const string Text = """
            VERDICT looks fine
            ```json
            { "confirmed": [ this is not valid json
            ```
            """;

        bool ok = SecondOpinionParser.TryParse(Text, out ParsedValidation? parsed, out string prose);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Equal(Text, prose);
    }

    [Fact]
    public void NoJsonBlockReturnsFalseAndKeepsProse()
    {
        const string Text = "CONFIRMED FINDINGS\n(none)\nVERDICT fine";
        Assert.False(SecondOpinionParser.TryParse(Text, out _, out string prose));
        Assert.Equal(Text, prose);
    }

    [Fact]
    public void LastJsonBlockWinsWhenSeveralArePresent()
    {
        const string Text = """
            ```json
            { "confirmed": [ { "severity": "low", "summary": "first" } ], "discarded": [], "verdict": "a" }
            ```
            more prose
            ```json
            { "confirmed": [ { "severity": "critical", "summary": "second" } ], "discarded": [], "verdict": "b" }
            ```
            """;

        Assert.True(SecondOpinionParser.TryParse(Text, out ParsedValidation? parsed, out _));
        Assert.Equal("second", Assert.Single(parsed!.Confirmed).Summary);
    }

    [Theory]
    [InlineData("critical", Severity.Critical)]
    [InlineData("HIGH", Severity.High)]
    [InlineData(" medium ", Severity.Medium)]
    [InlineData("low", Severity.Low)]
    [InlineData("", Severity.None)]
    [InlineData(null, Severity.None)]
    [InlineData("catastrophic", Severity.Medium)]
    public void SeverityParsesCaseInsensitivelyWithMediumFallback(string? label, Severity expected)
    {
        Assert.Equal(expected, SecondOpinionParser.ParseSeverity(label));
    }

    [Fact]
    public void MaxSeverityPicksTheHighest()
    {
        ConfirmedFinding[] findings =
        [
            new ConfirmedFinding("a", 1, "low", "x"),
            new ConfirmedFinding("b", 2, "critical", "y"),
            new ConfirmedFinding("c", 3, "medium", "z")
        ];

        Assert.Equal(Severity.Critical, SecondOpinionParser.MaxSeverity(findings));
    }

    [Fact]
    public void MaxSeverityOfNoFindingsIsNone()
    {
        Assert.Equal(Severity.None, SecondOpinionParser.MaxSeverity([]));
    }
}
