namespace Informant;

/// <summary>Depth policy applied to the candidate universe selected by review mode</summary>
public enum ReviewStrategy
{
    /// <summary>Deeply review every reviewable candidate</summary>
    Exhaustive,

    /// <summary>Allow full-file deferrals while preserving mandatory changed-content coverage</summary>
    Adaptive
}
