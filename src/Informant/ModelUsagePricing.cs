using BugSwatter.AI;

namespace Informant;

/// <summary>Optional USD rates per million input and output tokens for one configured model</summary>
public sealed record ModelUsagePricing(decimal? InputCostPerMillion, decimal? OutputCostPerMillion)
{
    /// <summary>True when both rates are omitted and the model is treated as local</summary>
    public bool IsLocal => InputCostPerMillion is null && OutputCostPerMillion is null;

    /// <summary>True when both rates are positive and completed provider usage can produce a cost estimate</summary>
    public bool CanEstimate => InputCostPerMillion is > 0 && OutputCostPerMillion is > 0;

    /// <summary>Estimates one completed response in USD, or returns null when rates or provider usage do not support a complete estimate</summary>
    public decimal? Estimate(ModelTokenUsage? usage)
    {
        if (!CanEstimate || usage?.PromptTokens is not >= 0 || usage.CompletionTokens is not >= 0)
        {
            return null;
        }

        try
        {
            return checked((usage.PromptTokens.Value * InputCostPerMillion!.Value + usage.CompletionTokens.Value * OutputCostPerMillion!.Value) / 1_000_000m);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    internal void Validate(string fieldName)
    {
        bool hasInputRate = InputCostPerMillion is not null;
        bool hasOutputRate = OutputCostPerMillion is not null;
        if (hasInputRate != hasOutputRate)
        {
            throw new InformantFatalException($"{fieldName}.inputCostPerMillion and {fieldName}.outputCostPerMillion must both be set or both be omitted");
        }

        if (InputCostPerMillion is < 0)
        {
            throw new InformantFatalException($"{fieldName}.inputCostPerMillion cannot be negative");
        }

        if (OutputCostPerMillion is < 0)
        {
            throw new InformantFatalException($"{fieldName}.outputCostPerMillion cannot be negative");
        }
    }
}
