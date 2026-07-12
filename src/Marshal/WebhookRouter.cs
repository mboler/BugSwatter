using System.Text.Json;

namespace Marshal;

/// <summary>Maps a validated webhook payload to the configured job it targets</summary>
public static class WebhookRouter
{
    private const int MaxTokenLength = 256;

    /// <summary>Extracts the provider's unique delivery ID: X-GitHub-Delivery for GitHub or the root id property for Azure DevOps</summary>
    public static string? ExtractDeliveryId(WebhookProvider provider, JsonElement payloadRoot, string? gitHubDeliveryHeader = null) => provider switch
    {
        WebhookProvider.GitHub => NormalizeToken(gitHubDeliveryHeader),
        WebhookProvider.AzureDevOps => ExtractRootString(payloadRoot, "id"),
        _ => null
    };

    /// <summary>Extracts the provider event type: X-GitHub-Event for GitHub or the root eventType property for Azure DevOps</summary>
    public static string? ExtractEventType(WebhookProvider provider, JsonElement payloadRoot, string? gitHubEventHeader = null) => provider switch
    {
        WebhookProvider.GitHub => NormalizeToken(gitHubEventHeader),
        WebhookProvider.AzureDevOps => ExtractRootString(payloadRoot, "eventType"),
        _ => null
    };

    /// <summary>True only for events that represent code pushed to a repository</summary>
    public static bool IsRepositoryChangeEvent(WebhookProvider provider, string eventType) => provider switch
    {
        WebhookProvider.GitHub => eventType.Equals("push", StringComparison.OrdinalIgnoreCase),
        WebhookProvider.AzureDevOps => eventType.Equals("git.push", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>True for provider setup probes that should succeed without enqueueing a review</summary>
    public static bool IsHandshakeEvent(WebhookProvider provider, string eventType) => provider == WebhookProvider.GitHub && eventType.Equals("ping", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts the repository identifier from a push payload: repository.full_name for GitHub, resource.repository.name (or remoteUrl) for Azure DevOps</summary>
    public static string? ExtractRepository(WebhookProvider provider, JsonElement payloadRoot)
    {
        switch (provider)
        {
            case WebhookProvider.GitHub:
                return payloadRoot.TryGetProperty("repository", out JsonElement repository) && repository.TryGetProperty("full_name", out JsonElement fullName) ? fullName.GetString() : null;

            case WebhookProvider.AzureDevOps:
                return !payloadRoot.TryGetProperty("resource", out JsonElement resource) || !resource.TryGetProperty("repository", out JsonElement adoRepository) ? null :
                    adoRepository.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } nameValue ? nameValue :
                    adoRepository.TryGetProperty("remoteUrl", out JsonElement remoteUrl) ? remoteUrl.GetString() : null;

            default:
                return null;
        }
    }

    /// <summary>Finds the job whose webhook mapping matches the provider and repository identifier, or null when none does</summary>
    public static ReviewJobConfig? MatchJob(IReadOnlyList<ReviewJobConfig> jobs, WebhookProvider provider, string repository)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(repository);

        return jobs.FirstOrDefault(job => job.Webhook is not null && job.Webhook.Provider == provider && string.Equals(job.Webhook.Repository, repository, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractRootString(JsonElement payloadRoot, string propertyName) =>
        payloadRoot.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String ? NormalizeToken(value.GetString()) : null;

    private static string? NormalizeToken(string? value)
    {
        string? normalized = value?.Trim();
        return normalized is { Length: > 0 and <= MaxTokenLength } ? normalized : null;
    }
}
