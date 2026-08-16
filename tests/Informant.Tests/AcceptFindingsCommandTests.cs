namespace Informant.Tests;

/// <summary>Finding acceptance command regression coverage</summary>
public sealed class AcceptFindingsCommandTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    /// <summary>Verifies the command accepts new findings from the latest matching artifact without model work</summary>
    [Fact]
    public void RunAcceptsLatestNewFindings()
    {
        FindingArtifactEntry finding = new("fingerprint", 1, FindingOrigin.Primary, "src/Example.cs", "src/Example.cs", 3, 3, "security", "high", "unsafe call", "{",
            "DangerousCall();", "}", FindingDisposition.New, FindingValidatorStatus.Confirmed);
        var artifact = new FindingRunArtifact(1, "repository", "main", "tip", DateTimeOffset.UnixEpoch, [finding], 0, 0, 0);
        FindingArtifactFile.Write(_directory.Path, "2026-08-15_12-00-00", artifact);
        string statePath = Path.Combine(_directory.Path, "findings.json");
        var config = new InformantConfig { RepositoryUrl = "repository", Branch = "main", ReportDirectory = _directory.Path, FindingStateFilePath = statePath };

        Assert.Equal(0, AcceptFindingsCommand.Run(config));
        Assert.Equal(0, AcceptFindingsCommand.Run(config));

        Assert.Equal("fingerprint", Assert.Single(new FindingStateStore(statePath).LoadSnapshot("repository", "main").Accepted).Fingerprint);
    }
}
