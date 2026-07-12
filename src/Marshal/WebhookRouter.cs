using System.Text.Json;

namespace Marshal;

/// <summary>Maps a validated webhook payload to the configured job it targets</summary>
public static class WebhookRouter
{
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
}
