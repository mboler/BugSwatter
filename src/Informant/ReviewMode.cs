namespace Informant;

/// <summary>Selects which file set a run reviews</summary>
public enum ReviewMode
{
    /// <summary>Review only files changed since the baseline SHA; a first run with no baseline reviews everything</summary>
    Changed,

    /// <summary>Review the entire tree, for periodic thorough sweeps</summary>
    Full
}
