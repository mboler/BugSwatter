using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BugSwatter.Common;

namespace Informant;

/// <summary>Versioned structural identity inputs and digest for one finding</summary>
public sealed record FindingIdentity(int Version, string Fingerprint, string Path, string Category, int ReportedLine, int AnchorLine, string PreviousAnchor, string SourceAnchor, string NextAnchor);

/// <summary>One valid inline BugSwatter suppression marker</summary>
public sealed record FindingSuppressionMarker(int MarkerLine, int? NextSignificantLine, string Justification)
{
    /// <summary>Whether this marker applies to the supplied structural anchor line</summary>
    public bool AppliesTo(int anchorLine) => anchorLine == MarkerLine || anchorLine == NextSignificantLine;
}

/// <summary>Suppression markers found while safely reading one source file</summary>
public sealed record FindingSuppressionScan(IReadOnlyList<FindingSuppressionMarker> Markers, IReadOnlyList<int> InvalidMarkerLines);

/// <summary>Creates finding fingerprints from normalized paths, categories, and nearby significant source</summary>
public static partial class FindingFingerprint
{
    /// <summary>Current structural fingerprint format</summary>
    public const int CurrentVersion = 1;

    /// <summary>Creates a stable identity without incorporating model prose</summary>
    public static FindingIdentity Create(string path, string? category, IReadOnlyList<string> lines, int? reportedLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(lines);

        string normalizedPath = RepositoryRelativePath.Normalize(path).Normalize(NormalizationForm.FormC);
        string normalizedCategory = NormalizeCategory(category);
        int effectiveReportedLine = lines.Count == 0 ? Math.Max(1, reportedLine ?? 1) : Math.Clamp(reportedLine ?? 1, 1, lines.Count);
        int anchorLine = FindNearestSignificantLine(lines, effectiveReportedLine);
        int previousLine = FindPreviousSignificantLine(lines, anchorLine);
        int nextLine = FindNextSignificantLine(lines, anchorLine);
        string previousAnchor = NormalizeSourceLine(lines, previousLine);
        string sourceAnchor = NormalizeSourceLine(lines, anchorLine);
        string nextAnchor = NormalizeSourceLine(lines, nextLine);
        string fingerprintInput = $"v{CurrentVersion}\n{normalizedPath}\n{normalizedCategory}\n{previousAnchor}\n{sourceAnchor}\n{nextAnchor}";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant();

        return new FindingIdentity(CurrentVersion, fingerprint, normalizedPath, normalizedCategory, effectiveReportedLine, anchorLine, previousAnchor, sourceAnchor, nextAnchor);
    }

    /// <summary>Finds valid and invalid inline suppression markers without interpreting one programming language</summary>
    public static FindingSuppressionScan ScanSuppressions(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var markers = new List<FindingSuppressionMarker>();
        var invalidLines = new List<int>();
        for (int index = 0; index < lines.Count; index++)
        {
            Match match = SuppressionRegex().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            string justification = match.Groups["justification"].Value.Trim();
            if (justification.Length == 0)
            {
                invalidLines.Add(index + 1);
                continue;
            }

            int nextSignificantLine = FindNextSignificantLine(lines, index + 1);
            markers.Add(new FindingSuppressionMarker(index + 1, nextSignificantLine == 0 ? null : nextSignificantLine, justification));
        }

        return new FindingSuppressionScan(markers, invalidLines);
    }

    private static string NormalizeCategory(string? category)
    {
        string value = string.IsNullOrWhiteSpace(category) ? "general" : category.Trim();
        return CollapseWhitespaceRegex().Replace(value.Normalize(NormalizationForm.FormC), " ").ToLowerInvariant();
    }

    private static int FindNearestSignificantLine(IReadOnlyList<string> lines, int reportedLine)
    {
        if (lines.Count == 0)
        {
            return 0;
        }

        for (int distance = 0; distance < lines.Count; distance++)
        {
            int before = reportedLine - distance;
            if (before >= 1 && !string.IsNullOrWhiteSpace(lines[before - 1]))
            {
                return before;
            }

            int after = reportedLine + distance;
            if (distance > 0 && after <= lines.Count && !string.IsNullOrWhiteSpace(lines[after - 1]))
            {
                return after;
            }
        }

        return 0;
    }

    private static int FindPreviousSignificantLine(IReadOnlyList<string> lines, int line)
    {
        for (int candidate = line - 1; candidate >= 1; candidate--)
        {
            if (!string.IsNullOrWhiteSpace(lines[candidate - 1]))
            {
                return candidate;
            }
        }

        return 0;
    }

    private static int FindNextSignificantLine(IReadOnlyList<string> lines, int line)
    {
        for (int candidate = line + 1; candidate <= lines.Count; candidate++)
        {
            if (!string.IsNullOrWhiteSpace(lines[candidate - 1]))
            {
                return candidate;
            }
        }

        return 0;
    }

    private static string NormalizeSourceLine(IReadOnlyList<string> lines, int line) => line <= 0 || line > lines.Count ? "" : CollapseWhitespaceRegex().Replace(lines[line - 1].Trim(), " ");

    [GeneratedRegex(@"^\s*(?:(?://+|#+|--+|;+|/\*+|\*+|<!--|<#+|'+|REM\b)\s*)bugswatter-ignore\s*:\s*(?<justification>.*?)(?:\s*(?:\*/|-->))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuppressionRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex CollapseWhitespaceRegex();
}
