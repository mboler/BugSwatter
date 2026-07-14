namespace Informant;

/// <summary>Informational assessment of a configured character budget against optional provider metadata</summary>
public sealed record ModelCapacityAdvisoryResult(bool Warning, string Detail);

/// <summary>Evaluates provider token metadata without replacing the operator's explicit character budget</summary>
public static class ModelCapacityAdvisory
{
    private const int EstimatedCharactersPerToken = 3;
    private const int InputBudgetPercent = 80;

    /// <summary>Returns a non-blocking capacity assessment for validation output</summary>
    public static ModelCapacityAdvisoryResult Evaluate(int maxContextCharacters, ModelCapacityMetadataResult metadata)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContextCharacters);
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.Status == ModelCapacityMetadataStatus.Unavailable)
        {
            return new ModelCapacityAdvisoryResult(false, $"optional LM Studio metadata is unavailable ({metadata.Detail}); configured maxContextCharacters remains authoritative");
        }

        if (metadata.Status == ModelCapacityMetadataStatus.Malformed)
        {
            return new ModelCapacityAdvisoryResult(true, $"optional LM Studio metadata is malformed ({metadata.Detail}); configured maxContextCharacters remains authoritative");
        }

        if (metadata.Status == ModelCapacityMetadataStatus.Contradictory)
        {
            return new ModelCapacityAdvisoryResult(true, $"optional LM Studio metadata is contradictory ({metadata.Detail}); configured maxContextCharacters remains authoritative");
        }

        if (!metadata.LoadedContextTokens.HasValue && !metadata.MaximumContextTokens.HasValue)
        {
            return new ModelCapacityAdvisoryResult(true, "optional model metadata was marked available without a context length; configured maxContextCharacters remains authoritative");
        }

        long capacityTokens = metadata.LoadedContextTokens ?? metadata.MaximumContextTokens!.Value;
        long estimatedInputTokens = (maxContextCharacters + (long)EstimatedCharactersPerToken - 1) / EstimatedCharactersPerToken;
        long advisoryInputLimit = capacityTokens * InputBudgetPercent / 100;
        string capacity = DescribeCapacity(metadata);
        if (estimatedInputTokens > advisoryInputLimit)
        {
            string detail = $"LM Studio reports {capacity}; maxContextCharacters {maxContextCharacters:N0} may exceed the {advisoryInputLimit:N0}-token input advisory after estimating "
                + $"{estimatedInputTokens:N0} tokens at {EstimatedCharactersPerToken} characters per token";
            return new ModelCapacityAdvisoryResult(true, detail);
        }

        string safeDetail = $"LM Studio reports {capacity}; maxContextCharacters {maxContextCharacters:N0} estimates {estimatedInputTokens:N0} tokens at {EstimatedCharactersPerToken} characters per token and remains "
            + $"below the {advisoryInputLimit:N0}-token input advisory";
        return new ModelCapacityAdvisoryResult(false, safeDetail);
    }

    private static string DescribeCapacity(ModelCapacityMetadataResult metadata)
    {
        if (metadata.LoadedContextTokens.HasValue && metadata.MaximumContextTokens.HasValue)
        {
            return $"{metadata.LoadedContextTokens:N0} loaded tokens and {metadata.MaximumContextTokens:N0} maximum tokens";
        }

        return metadata.LoadedContextTokens.HasValue ? $"{metadata.LoadedContextTokens:N0} loaded tokens" : $"{metadata.MaximumContextTokens:N0} maximum tokens";
    }
}
