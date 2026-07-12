using System.Text.Json;

namespace Marshal.Tests;

public sealed class WebhookRouterTests
{
    [Fact]
    public void ExtractsGitHubFullName()
    {
        using var payload = JsonDocument.Parse("""{"ref": "refs/heads/main", "repository": {"full_name": "mboler/SlimShady"}}""");
        Assert.Equal("mboler/SlimShady", WebhookRouter.ExtractRepository(WebhookProvider.GitHub, payload.RootElement));
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
            new ReviewJobConfig { Name = "other", SlimShadyConfigPath = "x", Webhook = new JobWebhookConfig { Provider = WebhookProvider.GitHub, Repository = "someone/else" } },
            new ReviewJobConfig { Name = "target", SlimShadyConfigPath = "y", Webhook = new JobWebhookConfig { Provider = WebhookProvider.GitHub, Repository = "MBoler/SlimShady" } }
        ];

        ReviewJobConfig? match = WebhookRouter.MatchJob(jobs, WebhookProvider.GitHub, "mboler/slimshady");
        Assert.NotNull(match);
        Assert.Equal("target", match.Name);
    }

    [Fact]
    public void ProviderMismatchDoesNotMatch()
    {
        ReviewJobConfig[] jobs = [new ReviewJobConfig { Name = "gh", SlimShadyConfigPath = "x", Webhook = new JobWebhookConfig { Provider = WebhookProvider.GitHub, Repository = "mboler/SlimShady" } }];
        Assert.Null(WebhookRouter.MatchJob(jobs, WebhookProvider.AzureDevOps, "mboler/SlimShady"));
    }

    [Fact]
    public void JobsWithoutWebhookAreIgnored()
    {
        ReviewJobConfig[] jobs = [new ReviewJobConfig { Name = "quiet", SlimShadyConfigPath = "x" }];
        Assert.Null(WebhookRouter.MatchJob(jobs, WebhookProvider.GitHub, "anything"));
    }
}
