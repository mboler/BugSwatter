using System.Net;
using System.Text.Json;

namespace Informant.Tests;

/// <summary>Controller-owned GitHub Check Run publication regression coverage</summary>
[Collection("Informant configuration environment")]
public sealed class GitHubCheckRunPublisherTests
{
    private const string TokenVariable = "INFORMANT_GITHUB_CHECK_TEST_TOKEN";

    /// <summary>Verifies only new non-discarded findings on changed lines become neutral-check annotations</summary>
    [Fact]
    public async Task PublishesOnlyEligibleChangedLineFindings()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, "{}");
        FindingArtifactEntry[] findings =
        [
            Entry("eligible", 3, FindingDisposition.New, FindingValidatorStatus.Confirmed, "high", "eligible\nsummary"),
            Entry("known", 3, FindingDisposition.Known, FindingValidatorStatus.Confirmed, "high", "known summary"),
            Entry("suppressed", 3, FindingDisposition.Suppressed, FindingValidatorStatus.NotRun, "high", "suppressed summary"),
            Entry("discarded", 3, FindingDisposition.New, FindingValidatorStatus.Discarded, "high", "discarded summary"),
            Entry("unchanged", 8, FindingDisposition.New, FindingValidatorStatus.Confirmed, "low", "unchanged summary")
        ];
        var artifact = new FindingRunArtifact(1, "repository", "main", "tip", DateTimeOffset.UnixEpoch, findings, 1, 0, 0);
        IReadOnlyList<FileReviewResult> results = [Result(new ChangedFile("src/Example.cs", ChangeKind.Modified, [new LineRange(3, 3)]))];
        string? original = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "test-token");
        try
        {
            GitHubCheckRunResult result = await new GitHubCheckRunPublisher(new HttpClient(handler)).PublishAsync(Config(), "abc123", artifact, results,
                "Informant-Findings-2026-08-15_12-00-00.json");

            Assert.Equal(new GitHubCheckRunResult(1, 1), result);
            Assert.Equal("https://api.github.com/repos/example/project/check-runs", Assert.Single(handler.RequestUris)!.AbsoluteUri);
            Assert.Equal("Bearer test-token", Assert.Single(handler.AuthorizationHeaders));
            using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
            Assert.Equal("abc123", body.RootElement.GetProperty("head_sha").GetString());
            Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("neutral", body.RootElement.GetProperty("conclusion").GetString());
            JsonElement annotation = Assert.Single(body.RootElement.GetProperty("output").GetProperty("annotations").EnumerateArray());
            Assert.Equal("src/Example.cs", annotation.GetProperty("path").GetString());
            Assert.Equal(3, annotation.GetProperty("start_line").GetInt32());
            Assert.Equal("warning", annotation.GetProperty("annotation_level").GetString());
            Assert.Equal("eligible summary", annotation.GetProperty("message").GetString());
            Assert.DoesNotContain("known summary", handler.RequestBodies[0]);
            Assert.DoesNotContain("suppressed summary", handler.RequestBodies[0]);
            Assert.DoesNotContain("discarded summary", handler.RequestBodies[0]);
            Assert.DoesNotContain("unchanged summary", handler.RequestBodies[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, original);
        }
    }

    /// <summary>Verifies one request remains bounded to GitHub's annotation limit</summary>
    [Fact]
    public async Task CapsAnnotationsAtGitHubRequestLimit()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, "{}");
        FindingArtifactEntry[] findings = [.. Enumerable.Range(1, 60).Select(line => Entry($"finding-{line}", line, FindingDisposition.New, FindingValidatorStatus.NotRun, "low", $"finding {line}"))];
        var artifact = new FindingRunArtifact(1, "repository", "main", "tip", DateTimeOffset.UnixEpoch, findings, 0, 0, 0);
        IReadOnlyList<FileReviewResult> results = [Result(new ChangedFile("src/Example.cs", ChangeKind.Modified, [new LineRange(1, 60)]))];
        string? original = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "test-token");
        try
        {
            GitHubCheckRunResult result = await new GitHubCheckRunPublisher(new HttpClient(handler)).PublishAsync(Config(), "abc123", artifact, results, "findings.json");

            Assert.Equal(GitHubCheckRunPublisher.MaxAnnotations, result.AnnotationCount);
            Assert.Equal(60, result.EligibleFindingCount);
            using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
            Assert.Equal(GitHubCheckRunPublisher.MaxAnnotations, body.RootElement.GetProperty("output").GetProperty("annotations").GetArrayLength());
            Assert.Contains("50 of 60 eligible", body.RootElement.GetProperty("output").GetProperty("summary").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, original);
        }
    }

    /// <summary>Verifies findings from full-review-only files remain local because they have no changed-line anchor</summary>
    [Fact]
    public async Task DoesNotAnnotateFullReviewOnlyFindings()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Created, "{}");
        FindingArtifactEntry[] findings = [Entry("full-review", 3, FindingDisposition.New, FindingValidatorStatus.Confirmed, "high", "full review summary")];
        var artifact = new FindingRunArtifact(1, "repository", "main", "tip", DateTimeOffset.UnixEpoch, findings, 1, 0, 0);
        IReadOnlyList<FileReviewResult> results = [Result(new ChangedFile("src/Example.cs", ChangeKind.FullReview, []))];
        string? original = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "test-token");
        try
        {
            GitHubCheckRunResult result = await new GitHubCheckRunPublisher(new HttpClient(handler)).PublishAsync(Config(), "abc123", artifact, results, "findings.json");

            Assert.Equal(new GitHubCheckRunResult(0, 0), result);
            using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
            Assert.Empty(body.RootElement.GetProperty("output").GetProperty("annotations").EnumerateArray());
            Assert.DoesNotContain("full review summary", handler.RequestBodies[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, original);
        }
    }

    /// <summary>Verifies a rejected API request reports bounded provider detail without exposing the token</summary>
    [Fact]
    public async Task ApiFailureDoesNotExposeToken()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Forbidden, "{\"message\":\"permission denied\"}");
        var artifact = new FindingRunArtifact(1, "repository", "main", "tip", DateTimeOffset.UnixEpoch, [], 0, 0, 0);
        string? original = Environment.GetEnvironmentVariable(TokenVariable);
        Environment.SetEnvironmentVariable(TokenVariable, "test-token");
        try
        {
            GitHubCheckRunException exception = await Assert.ThrowsAsync<GitHubCheckRunException>(() => new GitHubCheckRunPublisher(new HttpClient(handler)).PublishAsync(Config(), "abc123",
                artifact, [], "findings.json"));

            Assert.Contains("HTTP 403", exception.Message);
            Assert.Contains("permission denied", exception.Message);
            Assert.DoesNotContain("test-token", exception.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, original);
        }
    }

    private static GitHubCheckRunConfig Config() => new() { Repository = "example/project", Token = $"env:{TokenVariable}" };

    private static FileReviewResult Result(ChangedFile file) => new(file, FileReviewStatus.Reviewed, "findings", 1, 1, null, CandidateSeverityDetermined: true);

    private static FindingArtifactEntry Entry(string fingerprint, int line, FindingDisposition disposition, FindingValidatorStatus validatorStatus, string severity, string summary) =>
        new(fingerprint, 1, FindingOrigin.Primary, "src/Example.cs", "src/Example.cs", line, line, "security", severity, summary, "before", "anchor", "after", disposition, validatorStatus);
}
