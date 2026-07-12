namespace BugSwatter.Common;

/// <summary>Creates bounded one-line diagnostic excerpts without duplicating truncation rules</summary>
public static class TextSummary
{
    private const string TruncatedMarker = " [truncated]";

    /// <summary>Trims surrounding whitespace and limits the original text to <paramref name="maxCharacters"/>, appending a marker when truncated</summary>
    public static string Create(string text, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        string trimmed = text.Trim();
        return trimmed.Length <= maxCharacters ? trimmed : trimmed[..maxCharacters] + TruncatedMarker;
    }
}
