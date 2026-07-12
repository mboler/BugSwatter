namespace BugSwatter.AI;

/// <summary>One slice of an oversized file: 1-based inclusive line span; HardCut marks a split where no clean logical boundary existed</summary>
public sealed record SourceChunk(int StartLine, int EndLine, bool HardCut);

/// <summary>Splits oversized files at logical boundaries such as member closings and blank separator lines so a chunk
/// does not cut through a method when a boundary is available. Brace depth uses a deliberate character-count heuristic
/// that works for typical source and degrades to blank-line splitting for non-brace languages</summary>
public static class SourceChunker
{
    /// <summary>Overhead added per line for the number prefix when estimating prompt size</summary>
    private const int PerLineOverhead = 10;

    /// <summary>Splits <paramref name="lines"/> into chunks capped by <paramref name="maxLines"/> lines and roughly
    /// <paramref name="maxCharacters"/> characters; a file inside both limits comes back as one chunk</summary>
    /// <returns>Contiguous 1-based chunks covering every line exactly once, in file order</returns>
    public static IReadOnlyList<SourceChunk> Split(string[] lines, int maxLines, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);

        if (lines.Length == 0)
        {
            return [];
        }

        if (lines.Length <= maxLines && EstimateCharacters(lines, 0, lines.Length) <= maxCharacters)
        {
            return [new SourceChunk(1, lines.Length, false)];
        }

        bool[] isBoundary = ComputeBoundaries(lines);
        var chunks = new List<SourceChunk>();
        int start = 1;

        while (start <= lines.Length)
        {
            int hardLimit = FindHardLimit(lines, start, maxLines, maxCharacters);
            if (hardLimit >= lines.Length)
            {
                chunks.Add(new SourceChunk(start, lines.Length, false));
                break;
            }

            // Prefer the last clean boundary in the window; never cut below half a window to avoid confetti chunks
            int floor = start + Math.Max(1, (hardLimit - start + 1) / 2) - 1;
            int cut = -1;
            for (int line = hardLimit; line >= floor; line--)
            {
                if (isBoundary[line - 1])
                {
                    cut = line;
                    break;
                }
            }

            chunks.Add(cut > 0 ? new SourceChunk(start, cut, false) : new SourceChunk(start, hardLimit, true));
            start = (cut > 0 ? cut : hardLimit) + 1;
        }

        return chunks;
    }

    private static bool[] ComputeBoundaries(string[] lines)
    {
        var boundaries = new bool[lines.Length];
        int depth = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();
            foreach (char character in trimmed)
            {
                switch (character)
                {
                    case '{':
                        depth++;
                        break;
                    
                    case '}':
                        depth--;
                        break;
                }
            }

            // A closing brace back at type or member level ends a method or type; a blank line at file or type level separates members
            boundaries[index] = (trimmed is "}" or "};" && depth <= 2) || (trimmed.Length == 0 && depth <= 1);
        }

        return boundaries;
    }

    private static int FindHardLimit(string[] lines, int start, int maxLines, int maxCharacters)
    {
        int limit = Math.Min(start + maxLines - 1, lines.Length);
        int characters = 0;

        for (int line = start; line <= limit; line++)
        {
            characters += lines[line - 1].Length + PerLineOverhead;
            if (characters > maxCharacters && line > start)
            {
                return line - 1;
            }
        }

        return limit;
    }

    private static int EstimateCharacters(string[] lines, int startIndex, int count)
    {
        int characters = 0;
        for (int index = startIndex; index < startIndex + count; index++)
        {
            characters += lines[index].Length + PerLineOverhead;
        }

        return characters;
    }
}
