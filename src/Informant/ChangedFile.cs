namespace Informant;

/// <summary>Inclusive 1-based line range on the new side of a diff</summary>
public sealed record LineRange(int Start, int End)
{
    /// <summary>Formats the range as "12" or "12-18" for prompts, logs and reports</summary>
    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";
}

/// <summary>How a file entered the review set</summary>
public enum ChangeKind
{
    /// <summary>File added since the baseline</summary>
    Added,

    /// <summary>File modified since the baseline</summary>
    Modified,

    /// <summary>File renamed since the baseline; ranges cover any content edits made with the rename</summary>
    Renamed,

    /// <summary>File deleted since the baseline; content is read from the recorded baseline Git object</summary>
    Deleted,

    /// <summary>File selected by a full-tree review; there is no changed-range focus</summary>
    FullReview
}

/// <summary>One file to review, with changed ranges and the immutable Git revision supplying deleted content when applicable</summary>
public sealed record ChangedFile(string Path, ChangeKind Kind, IReadOnlyList<LineRange> ChangedRanges, string? ContentRevision = null);
