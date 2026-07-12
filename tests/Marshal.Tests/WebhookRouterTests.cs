using System.Text.Json;

namespace Marshal.Tests;

public sealed class WebhookRouterTests
{
    [Fact]
    public void ExtractsGitHubHeadersAsDeliveryMetadata()
    {
        using var payload = JsonDocument.Parse("{}");

        Assert.Equal("delivery-id", WebhookRouter.ExtractDeliveryId(WebhookProvider.GitHub, payload.RootElement, " delivery-id "));
        Assert.Equal("push", WebhookRouter.ExtractEventType(WebhookProvider.GitHub, payload.RootElement, "push"));
    }

    [Fact]
    public void ExtractsAzureDevOpsPayloadDeliveryMetadata()
    {
        using var payload = JsonDocument.Parse("""{"id":"a0a0a0a0-bbbb-cccc-dddd-e1e1e1e1e1e1","eventType":"git.push"}""");

        Assert.Equal("a0a0a0a0-bbbb-cccc-dddd-e1e1e1e1e1e1", WebhookRouter.ExtractDeliveryId(WebhookProvider.AzureDevOps, payload.RootElement));
        Assert.Equal("git.push", WebhookRouter.ExtractEventType(WebhookProvider.AzureDevOps, payload.RootElement));
    }

    [Theory]
    [InlineData(WebhookProvider.GitHub, "push", true)]
    [InlineData(WebhookProvider.GitHub, "pull_request", false)]
    [InlineData(WebhookProvider.AzureDevOps, "git.push", true)]
    [InlineData(WebhookProvider.AzureDevOps, "git.pullrequest.created", false)]
    public void OnlyPushEventsAreRepositoryChanges(WebhookProvider provider, string eventType, bool expected)
    {
        Assert.Equal(expected, WebhookRouter.IsRepositoryChangeEvent(provider, eventType));
    }

    [Fact]
    public void GitHubPingIsAHandshakeButPushIsNot()
    {
        Assert.True(WebhookRouter.IsHandshakeEvent(WebhookProvider.GitHub, "ping"));
        Assert.False(WebhookRouter.IsHandshakeEvent(WebhookProvider.GitHub, "push"));
        Assert.False(WebhookRouter.IsHandshakeEvent(WebhookProvider.AzureDevOps, "ping"));
    }

    [Fact]
    public void ExtractsGitHubFullName()
    {
        using var payload = JsonDocument.Parse("""{"ref": "refs/heads/main", "repository": {"full_name": "mboler/BugSwatter"}}""");
        Assert.Equal("mboler/BugSwatter", WebhookRouter.ExtractRepository(WebhookProvider.GitHub, payload.RootElement));
    }

    [Fact]
    public void ExtractsAzureDevOpsRepositoryName()
    {
        using var payload = JsonDocument.Parse("""{"eventType": "git.push", "resource": {"repository": {"name": "MyRepo", "remoteUrl": "https://dev.azure.com/org/proj/_git/MyRepo"}}}""");
        Assert.Equal("MyRepo", WebhookRouter.ExtractRepository(WebhookProvider.AzureDevOps, payload.RootElement));
    }

    [Fact]
    public void FallsBackToAzureDevOpsRemoteUrl()
    {
        using var payload = JsonDocument.Parse("""{"resource": {"repository": {"name": "", "remoteUrl": "https://dev.azure.com/org/proj/_git/MyRepo"}}}""");
        Assert.Equal("https://dev.azure.com/org/proj/_git/MyRepo", WebhookRouter.ExtractRepository(WebhookProvider.AzureDevOps, payload.RootElement));
    }

    [Fact]
    public void MissingRepositoryYieldsNull()
    {
        using var payload = JsonDocument.Parse("""{"zen": "Design for failure."}""");
        Assert.Null(WebhookRouter.ExtractRepository(WebhookProvider.GitHub, payload.RootElement));
    }

    [Fact]
    public void MatchesJobCaseInsensitively()
    {
        ReviewJobConfig[] jobs =
        [
            new ReviewJobConfig { Name = "other", InformantConfigPath = "x", Webhook = new JobWebhookConfig { Provider = WebhookProvider.GitHub, Repository = "someone/else" } },
            new ReviewJobConfig { Name = "target", InformantConfigPath = "y", Webhook = new JobWebhookConfig { Provider = WebhookProvider.GitHub, Repository = "MBoler/BugSwatter" } }
        ];

        ReviewJobConfig? match = WebhookRouter.MatchJob(jobs, WebhookProvider.GitHub, "mboler/bugswatter");
        Assert.NotNull(match);
        Assert.Equal("target", match.Name);
    }

    [Fact]
    public void ProviderMismatchDoesNotMatch()
    {
        ReviewJobConfig[] jobs = [new ReviewJobConfig { Name = "gh", InformantConfigPath = "x", Webhook = new JobWebhookConfig { Provider = WebhookProvider.GitHub, Repository = "mboler/BugSwatter" } }];
        Assert.Null(WebhookRouter.MatchJob(jobs, WebhookProvider.AzureDevOps, "mboler/BugSwatter"));
    }

    [Fact]
    public void JobsWithoutWebhookAreIgnored()
    {
        ReviewJobConfig[] jobs = [new ReviewJobConfig { Name = "quiet", InformantConfigPath = "x" }];
        Assert.Null(WebhookRouter.MatchJob(jobs, WebhookProvider.GitHub, "anything"));
    }
}
