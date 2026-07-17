namespace Informant;

/// <summary>One already-running OpenAI-compatible model that Informant may use after the preferred primary model becomes unavailable</summary>
public sealed record FallbackModelConfig
{
    /// <summary>Short label written to logs, progress snapshots, and reports</summary>
    public string Name { get; init; } = "";

    /// <summary>OpenAI-compatible base URL of the fallback endpoint</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Model identifier sent to the fallback endpoint, or * to select its single loaded LM Studio model</summary>
    public string ModelName { get; init; } = "";

    /// <summary>USD cost per million input tokens; omit with outputCostPerMillion for a local model</summary>
    public decimal? InputCostPerMillion { get; init; }

    /// <summary>USD cost per million output tokens; omit with inputCostPerMillion for a local model</summary>
    public decimal? OutputCostPerMillion { get; init; }

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

        new ModelUsagePricing(InputCostPerMillion, OutputCostPerMillion).Validate(fieldName);
    }
}

/// <summary>Effective primary-review target, including the preferred model followed by each configured fallback</summary>
public sealed record PrimaryModelTarget(string Name, string Endpoint, string ModelName, bool IsFallback, decimal? InputCostPerMillion = null, decimal? OutputCostPerMillion = null)
{
    /// <summary>Cost classification and rates carried with this resolved model target</summary>
    public ModelUsagePricing Pricing => new(InputCostPerMillion, OutputCostPerMillion);
}
