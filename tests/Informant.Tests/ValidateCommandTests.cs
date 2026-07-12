using System.Net;

namespace Informant.Tests;

public sealed class ValidateCommandTests
{
    [Fact]
    public async Task AllChecksPassWhenEndpointAnswersAndSecretsResolve()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        handler.Enqueue(HttpStatusCode.OK, "{}");

        InformantConfig config = BuildConfig(withSecondOpinion: true, apiKeyVar: "INFORMANT_VALIDATE_KEY");

        Environment.SetEnvironmentVariable("INFORMANT_VALIDATE_KEY", "present");
        try
        {
            IReadOnlyList<ValidationCheck> checks = await ValidateCommand.GatherChecksAsync(config, new HttpClient(handler));
            Assert.All(checks, check => Assert.True(check.Passed, $"{check.Label}: {check.Detail}"));
            Assert.Contains(checks, check => check.Label == "second-opinion endpoint reachable");
            Assert.Contains(checks, check => check.Label == "second-opinion API key");
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_VALIDATE_KEY", null);
        }
    }

    [Fact]
    public async Task UnreachableEndpointFailsItsCheck()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));

        InformantConfig config = BuildConfig(withSecondOpinion: false, apiKeyVar: null);
        IReadOnlyList<ValidationCheck> checks = await ValidateCommand.GatherChecksAsync(config, new HttpClient(handler));

        ValidationCheck endpoint = Assert.Single(checks, check => check.Label == "model endpoint reachable");
        Assert.False(endpoint.Passed);
        Assert.Contains("did not answer", endpoint.Detail);
    }

    [Fact]
    public async Task MissingSecondOpinionKeyFailsItsCheck()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        handler.Enqueue(HttpStatusCode.OK, "{}");

        InformantConfig config = BuildConfig(withSecondOpinion: true, apiKeyVar: "INFORMANT_VALIDATE_UNSET");
        Environment.SetEnvironmentVariable("INFORMANT_VALIDATE_UNSET", null);

        IReadOnlyList<ValidationCheck> checks = await ValidateCommand.GatherChecksAsync(config, new HttpClient(handler));

        ValidationCheck key = Assert.Single(checks, check => check.Label == "second-opinion API key");
        Assert.False(key.Passed);
        Assert.Contains("is not set", key.Detail);
    }

    [Fact]
    public async Task KeylessLocalSecondOpinionAddsNoKeyCheck()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        handler.Enqueue(HttpStatusCode.OK, "{}");

        InformantConfig config = BuildConfig(withSecondOpinion: true, apiKeyVar: null);
        IReadOnlyList<ValidationCheck> checks = await ValidateCommand.GatherChecksAsync(config, new HttpClient(handler));

        Assert.DoesNotContain(checks, check => check.Label == "second-opinion API key");
        Assert.Contains(checks, check => check.Label == "second-opinion endpoint reachable");
    }

    private static InformantConfig BuildConfig(bool withSecondOpinion, string? apiKeyVar) => new()
    {
        RepositoryUrl = "https://example.test/repo.git",
        Branch = "main",
        WorkingTreePath = @"C:\informant\tree",
        GitExecutablePath = TestGit.ExecutablePath,
        ModelEndpoint = "http://localhost:1234/v1",
        ModelName = "test-model",
        SecondOpinion = withSecondOpinion ? new SecondOpinionConfig { Endpoint = "http://localhost:1235/v1", ModelName = "validator", ApiKey = apiKeyVar is null ? null : $"env:{apiKeyVar}" } : null
    };
}
