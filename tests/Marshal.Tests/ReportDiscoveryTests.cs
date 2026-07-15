namespace Marshal.Tests;

public sealed class ReportDiscoveryTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void DiscoversNewestReportFromCommentedConfig()
    {
        // Informant's own loader accepts comments, so job configs in the wild carry them; discovery must too
        string configPath = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(configPath, "{\n  // reports land here\n  \"reportDirectory\": \"reports\",\n}\n");

        string reportsDirectory = Path.Combine(_directory.Path, "reports");
        Directory.CreateDirectory(reportsDirectory);
        string oldReport = Path.Combine(reportsDirectory, "Informant-Report-2026-07-09_01-00-00.md");
        string newReport = Path.Combine(reportsDirectory, "Informant-Report-2026-07-10_02-00-00.md");
        File.WriteAllText(oldReport, "old");
        File.WriteAllText(newReport, "new");
        File.SetLastWriteTimeUtc(oldReport, DateTime.UtcNow.AddHours(-10));
        File.SetLastWriteTimeUtc(newReport, DateTime.UtcNow);

        string? discovered = InformantProcessRunner.DiscoverReportPath(configPath, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(newReport, discovered);
    }

    [Fact]
    public void ReportsOlderThanTheRunAreNotAttributedToIt()
    {
        string configPath = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(configPath, """{ "reportDirectory": "reports" }""");

        string reportsDirectory = Path.Combine(_directory.Path, "reports");
        Directory.CreateDirectory(reportsDirectory);
        string staleReport = Path.Combine(reportsDirectory, "Informant-Report-2026-07-01_01-00-00.md");
        File.WriteAllText(staleReport, "stale");
        File.SetLastWriteTimeUtc(staleReport, DateTime.UtcNow.AddDays(-9));

        Assert.Null(InformantProcessRunner.DiscoverReportPath(configPath, DateTime.UtcNow.AddMinutes(-5)));
    }

    [Fact]
    public void ReportDiscoveryUsesInformantEnvironmentOverride()
    {
        string configPath = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(configPath, """{ "reportDirectory": "ignored" }""");
        string overriddenDirectory = Path.Combine(_directory.Path, "environment-reports");
        Directory.CreateDirectory(overriddenDirectory);
        string report = Path.Combine(overriddenDirectory, "Informant-Report-2026-07-12_10-00-00.md");
        File.WriteAllText(report, "report");
        string? originalReportDirectory = Environment.GetEnvironmentVariable("INFORMANT_ReportDirectory");
        Environment.SetEnvironmentVariable("INFORMANT_ReportDirectory", overriddenDirectory);
        try
        {
            Assert.Equal(report, InformantProcessRunner.DiscoverReportPath(configPath, DateTime.UtcNow.AddMinutes(-1)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ReportDirectory", originalReportDirectory);
        }
    }

    [Fact]
    public void MissingConfigOrDirectoryYieldsNullNotAnException()
    {
        Assert.Null(InformantProcessRunner.DiscoverReportPath(Path.Combine(_directory.Path, "ghost.json"), DateTime.UtcNow));
    }

    [Fact]
    public void ParseReportMarkerReadsAnEmittedPath()
    {
        (bool found, string? path) = InformantProcessRunner.ParseReportMarker("some log noise\nINFORMANT-REPORT: C:\\reports\\r-validated.md\n");
        Assert.True(found);
        Assert.Equal("C:\\reports\\r-validated.md", path);
    }

    [Fact]
    public void ParseReportMarkerReadsNoneAsFoundWithNullPath()
    {
        (bool found, string? path) = InformantProcessRunner.ParseReportMarker("INFORMANT-REPORT: none");
        Assert.True(found);
        Assert.Null(path);
    }

    [Fact]
    public void ParseReportMarkerReportsNotFoundWhenAbsent()
    {
        (bool found, string? path) = InformantProcessRunner.ParseReportMarker("no marker here\njust output\n");
        Assert.False(found);
        Assert.Null(path);
    }

    [Fact]
    public void ResolveReportPathTrustsANoneMarkerOverAStaleReport()
    {
        // The whole point of the marker: a no-change run says "none", so a fresh report left in the directory by a
        // different run is never mis-attributed to this one the way timestamp discovery could
        string configPath = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(configPath, """{ "reportDirectory": "reports" }""");
        string reportsDirectory = Path.Combine(_directory.Path, "reports");
        Directory.CreateDirectory(reportsDirectory);
        File.WriteAllText(Path.Combine(reportsDirectory, "Informant-Report-2026-07-11_10-00-00.md"), "fresh");

        Assert.Null(InformantProcessRunner.ResolveReportPath("INFORMANT-REPORT: none", configPath, DateTime.UtcNow));
    }

    [Fact]
    public void ResolveReportPathPrefersTheMarkerPath()
    {
        Assert.Equal("C:\\reports\\chosen-validated.md", InformantProcessRunner.ResolveReportPath("INFORMANT-REPORT: C:\\reports\\chosen-validated.md", "ignored.json", DateTime.UtcNow));
    }

    [Fact]
    public void ResolveReportPathFallsBackToDiscoveryWithoutAMarker()
    {
        string configPath = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(configPath, """{ "reportDirectory": "reports" }""");
        string reportsDirectory = Path.Combine(_directory.Path, "reports");
        Directory.CreateDirectory(reportsDirectory);
        string report = Path.Combine(reportsDirectory, "Informant-Report-2026-07-11_10-00-00.md");
        File.WriteAllText(report, "r");

        Assert.Equal(report, InformantProcessRunner.ResolveReportPath("no marker at all", configPath, DateTime.UtcNow.AddMinutes(-5)));
    }
}
