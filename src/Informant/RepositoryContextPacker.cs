namespace Informant;

/// <summary>One independently selectable repository context block</summary>
public sealed record RepositoryContextItem(string Id, int Priority, string Content, bool Required = false);

/// <summary>Deterministic bounded context selection with explicit omissions</summary>
public sealed record RepositoryContextPack(string Text, IReadOnlyList<RepositoryContextItem> Selected, IReadOnlyList<RepositoryContextItem> Omitted, int UsedCharacters, int CharacterBudget)
{
    /// <summary>Required items that could not fit within the configured budget</summary>
    public IReadOnlyList<RepositoryContextItem> OmittedRequired => [.. Omitted.Where(item => item.Required)];
}

/// <summary>Packs whole context blocks by priority without splitting or silently dropping required material</summary>
public static class RepositoryContextPacker
{
    private const int SeparatorCharacters = 2;

    /// <summary>Selects the highest-priority blocks that fit and explicitly returns every omitted block</summary>
    public static RepositoryContextPack Pack(IEnumerable<RepositoryContextItem> items, int characterBudget)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterBudget);

        RepositoryContextItem[] ordered =
        [
            .. items
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
        ];
        Validate(ordered);

        var selected = new List<RepositoryContextItem>();
        var omitted = new List<RepositoryContextItem>();
        int usedCharacters = 0;
        foreach (RepositoryContextItem item in ordered)
        {
            int separator = selected.Count == 0 ? 0 : SeparatorCharacters;
            if (item.Content.Length <= characterBudget - usedCharacters - separator)
            {
                selected.Add(item);
                usedCharacters += separator + item.Content.Length;
            }
            else
            {
                omitted.Add(item);
            }
        }

        string text = string.Join("\n\n", selected.Select(item => item.Content));
        return new RepositoryContextPack(text, selected, omitted, usedCharacters, characterBudget);
    }

    private static void Validate(IReadOnlyList<RepositoryContextItem> items)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (RepositoryContextItem item in items)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
            ArgumentNullException.ThrowIfNull(item.Content);
            ArgumentOutOfRangeException.ThrowIfNegative(item.Priority);
            if (!identifiers.Add(item.Id))
            {
                throw new ArgumentException($"Context item identifier is duplicated: '{item.Id}'", nameof(items));
            }
        }
    }
}
