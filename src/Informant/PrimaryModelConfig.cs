namespace Informant;

/// <summary>One already-running OpenAI-compatible model that Informant may use after the preferred primary model becomes unavailable</summary>
public sealed record FallbackModelConfig
{
    /// <summary>Short label written to logs, progress snapshots, and reports</summary>
    public string Name { get; init; } = "";

    /// <summary>OpenAI-compatible base URL of the fallback endpoint</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Model identifier sent to the fallback endpoint</summary>
    public string ModelName { get; init; } = "";

    internal void Validate(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InformantFatalException($"{fieldName}.name is required");
        }

        if (Name.Equals("primary", StringComparison.OrdinalIgnoreCase))
        {
            throw new InformantFatalException($"{fieldName}.name cannot be 'primary', which is reserved for the preferred model");
        }

        if (string.IsNullOrWhiteSpace(Endpoint) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InformantFatalException($"{fieldName}.endpoint must be an absolute http or https URL, got '{Endpoint}'");
        }

        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new InformantFatalException($"{fieldName}.modelName is required");
        }
    }
}

/// <summary>Effective primary-review target, including the preferred model followed by each configured fallback</summary>
public sealed record PrimaryModelTarget(string Name, string Endpoint, string ModelName, bool IsFallback);
