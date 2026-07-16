namespace Informant;

/// <summary>Configured primary-review target after optional loaded-model discovery</summary>
public sealed record PrimaryModelTargetResolution(bool Succeeded, PrimaryModelTarget Target, string Detail);

/// <summary>Resolves the explicit loaded-model wildcard without changing model-server state</summary>
public static class PrimaryModelTargetResolver
{
    /// <summary>Configuration value that requests the single language model already loaded at an LM Studio endpoint</summary>
    public const string LoadedModelWildcard = "*";

    /// <summary>Returns the unchanged explicit target or replaces the wildcard with one discovered model identifier</summary>
    public static async Task<PrimaryModelTargetResolution> ResolveAsync(HttpClient http, PrimaryModelTarget target, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        if (!string.Equals(target.ModelName, LoadedModelWildcard, StringComparison.Ordinal))
        {
            return new PrimaryModelTargetResolution(true, target, $"using configured model '{target.ModelName}'");
        }

        LoadedModelDiscoveryResult discovery = await LmStudioLoadedModelDiscovery.DiscoverAsync(http, target.Endpoint, timeout, cancellationToken);
        if (discovery.Status != LoadedModelDiscoveryStatus.Available || string.IsNullOrWhiteSpace(discovery.ModelName))
        {
            return new PrimaryModelTargetResolution(false, target, discovery.Detail);
        }

        return new PrimaryModelTargetResolution(true, target with { ModelName = discovery.ModelName }, discovery.Detail);
    }
}
