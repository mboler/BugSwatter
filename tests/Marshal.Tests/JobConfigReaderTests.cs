namespace Marshal.Tests;

public sealed class JobConfigReaderTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ReadsCommentedJsonWithTrailingCommas()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, """
            {
              // local endpoint
              "modelEndpoint": "http://localhost:1234/v1",
            }
            """);

        Assert.Equal("http://localhost:1234/v1", JobConfigReader.TryReadModelEndpoint(path));
    }

    [Fact]
    public void ReadsEffectiveEnvironmentOverride()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, """{ "modelEndpoint": "http://json.example/v1" }""");
        Environment.SetEnvironmentVariable("INFORMANT_ModelEndpoint", "http://environment.example/v1");
        try
        {
            Assert.Equal("http://environment.example/v1", JobConfigReader.TryReadModelEndpoint(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ModelEndpoint", null);
        }
    }

    [Fact]
    public void ReadsRepositoryPollingTargetAndResolvesRelativePaths()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, """
            {
              "repositoryUrl": "https://example.test/owner/repository.git",
              "branch": "develop",
              "gitExecutablePath": "tools/git.exe",
              "stateFilePath": "state/reviews.json"
            }
            """);

        RepositoryPollTarget target = JobConfigReader.TryReadRepositoryPollTarget(path)!;

        Assert.Equal("https://example.test/owner/repository.git", target.RepositoryUrl);
        Assert.Equal("develop", target.Branch);
        Assert.Equal(Path.Combine(_directory.Path, "tools", "git.exe"), target.GitExecutablePath);
        Assert.Equal(Path.Combine(_directory.Path, "state", "reviews.json"), target.StateFilePath);
    }

    [Fact]
    public void BareGitCommandRemainsOnPath()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, """{ "repositoryUrl": "https://example.test/repository.git", "branch": "main", "gitExecutablePath": "git" }""");

        Assert.Equal("git", JobConfigReader.TryReadRepositoryPollTarget(path)!.GitExecutablePath);
    }

    [Fact]
    public void ReadsMatchingCompletedBaseline()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        string statePath = Path.Combine(_directory.Path, "state.json");
        File.WriteAllText(path, """{ "repositoryUrl": "https://example.test/repository.git", "branch": "main", "stateFilePath": "state.json" }""");
        File.WriteAllText(statePath, """
            {
              "https://example.test/repository.git|main": {
                "sha": "1234567890123456789012345678901234567890",
                "updatedUtc": "2026-07-12T12:00:00Z"
              }
            }
            """);

        RepositoryPollTarget target = JobConfigReader.TryReadRepositoryPollTarget(path)!;

        Assert.Equal("1234567890123456789012345678901234567890", JobConfigReader.ReadBaselineSha(target));
    }

    [Fact]
    public void MissingStateFileMeansNoCompletedBaseline()
    {
        var target = new RepositoryPollTarget("https://example.test/repository.git", "main", "git", Path.Combine(_directory.Path, "missing.json"));

        Assert.Null(JobConfigReader.ReadBaselineSha(target));
    }

    [Fact]
    public void CorruptStateFileIsReported()
    {
        string statePath = Path.Combine(_directory.Path, "state.json");
        File.WriteAllText(statePath, "not-json");
        var target = new RepositoryPollTarget("https://example.test/repository.git", "main", "git", statePath);

        Assert.Throws<InvalidDataException>(() => JobConfigReader.ReadBaselineSha(target));
    }

    [Fact]
    public void MissingRepositoryOrBranchCannotBecomePollingTarget()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, "{}");

        Assert.Null(JobConfigReader.TryReadRepositoryPollTarget(path));
    }
}
