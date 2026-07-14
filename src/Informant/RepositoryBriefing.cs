using System.Text;
using System.Text.RegularExpressions;
using BugSwatter.Common;

namespace Informant;

/// <summary>Code-agnostic roles used to prioritize repository material for planning</summary>
[Flags]
public enum RepositoryBriefingRole
{
    /// <summary>No role assigned</summary>
    None = 0,

    /// <summary>Root-level repository guidance or contributor documentation</summary>
    Guidance = 1,

    /// <summary>Root-level build, package, deployment, or configuration metadata</summary>
    BuildManifest = 2,

    /// <summary>Content matched by an explicit configured seed</summary>
    Seed = 4,

    /// <summary>Content selected by the current change set</summary>
    Changed = 8,

    /// <summary>Ordinary reviewable repository content</summary>
    RepositoryContent = 16,

    /// <summary>Manifest metadata for content that cannot be reviewed</summary>
    Excluded = 32
}

/// <summary>One manifest entry classified for deterministic planning priority</summary>
public sealed record RepositoryBriefingEntry(RepositoryManifestEntry ManifestEntry, RepositoryBriefingRole Roles, int Priority);

/// <summary>One bounded exact-path manifest representation suitable for a planning input</summary>
public sealed record RepositoryManifestPartition(int Number, string Text, IReadOnlyList<string> Paths, bool WithinCharacterLimit);

/// <summary>Code-agnostic repository metadata prepared for a model planning pass</summary>
public sealed record RepositoryBriefing(string Summary, string DirectorySummary, IReadOnlyList<RepositoryBriefingEntry> Entries, IReadOnlyList<RepositoryManifestPartition> ManifestPartitions,
    IReadOnlyList<string> UnmatchedSeedPaths);

/// <summary>Builds deterministic role-ranked repository briefing metadata without interpreting source languages</summary>
public sealed class RepositoryBriefingBuilder
{
    private const int GuidancePriority = 10;
    private const int BuildManifestPriority = 20;
    private const int SeedPriority = 30;
    private const int ChangedPriority = 40;
    private const int RepositoryContentPriority = 50;
    private const int ExcludedPriority = 60;

    private static readonly HashSet<string> GuidanceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AGENTS.md",
        "CLAUDE.md"
    };

    private static readonly HashSet<string> BuildManifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".editorconfig",
        ".gitignore",
        "CMakeLists.txt",
        "Cargo.toml",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "go.mod",
        "Makefile",
        "package.json",
        "pnpm-workspace.yaml",
        "pom.xml",
        "pyproject.toml",
        "requirements.txt"
    };

    private static readonly HashSet<string> BuildManifestExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj",
        ".fsproj",
        ".sln",
        ".slnx",
        ".vbproj"
    };

    /// <summary>Classifies the manifest, expands safe seed matches, and creates bounded exact-path partitions</summary>
    public RepositoryBriefing Build(RepositoryManifest manifest, IReadOnlyList<string> seedPaths, int maxManifestPartitionCharacters)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(seedPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxManifestPartitionCharacters, 256);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        SeedPattern[] patterns = [.. seedPaths.Select(SeedPattern.Create).DistinctBy(pattern => pattern.Pattern, comparer)];
        var matchedSeeds = new HashSet<string>(comparer);
        var entries = new List<RepositoryBriefingEntry>(manifest.Entries.Count);

        foreach (RepositoryManifestEntry entry in manifest.Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            RepositoryBriefingRole roles = Classify(entry);
            foreach (SeedPattern pattern in patterns)
            {
                if (!pattern.IsMatch(entry.Path))
                {
                    continue;
                }

                if (entry.Disposition is RepositoryManifestDisposition.SymbolicLink or RepositoryManifestDisposition.ReparsePoint)
                {
                    throw new InformantFatalException($"seedPaths entry '{pattern.Pattern}' matched symbolic-link or reparse-point path '{entry.Path}'");
                }

                matchedSeeds.Add(pattern.Pattern);
                roles |= RepositoryBriefingRole.Seed;
            }

            entries.Add(new RepositoryBriefingEntry(entry, roles, Priority(roles)));
        }

        RepositoryBriefingEntry[] orderedEntries =
        [
            .. entries
                .OrderBy(entry => entry.Priority)
                .ThenBy(entry => entry.ManifestEntry.Path, StringComparer.Ordinal)
        ];
        string[] unmatchedSeeds = [.. patterns.Select(pattern => pattern.Pattern).Where(pattern => !matchedSeeds.Contains(pattern)).OrderBy(pattern => pattern, StringComparer.Ordinal)];
        string summary = BuildSummary(manifest, orderedEntries, unmatchedSeeds.Length);
        string directorySummary = BuildDirectorySummary(orderedEntries);
        IReadOnlyList<RepositoryManifestPartition> partitions = BuildPartitions(orderedEntries, maxManifestPartitionCharacters);
        return new RepositoryBriefing(summary, directorySummary, orderedEntries, partitions, unmatchedSeeds);
    }

    private static RepositoryBriefingRole Classify(RepositoryManifestEntry entry)
    {
        RepositoryBriefingRole roles = entry.Reviewable ? RepositoryBriefingRole.RepositoryContent : RepositoryBriefingRole.Excluded;
        if (entry.ChangeKind is not null)
        {
            roles |= RepositoryBriefingRole.Changed;
        }

        if (!entry.RootLevel)
        {
            return roles;
        }

        string name = entry.Path;
        if (IsGuidance(name))
        {
            roles |= RepositoryBriefingRole.Guidance;
        }

        if (IsBuildManifest(name))
        {
            roles |= RepositoryBriefingRole.BuildManifest;
        }

        return roles;
    }

    private static bool IsGuidance(string name) => GuidanceNames.Contains(name) || name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CONTRIBUTING", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SECURITY", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildManifest(string name) => BuildManifestNames.Contains(name) || BuildManifestExtensions.Contains(Path.GetExtension(name))
        || name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("compose.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("docker-compose.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("requirements-", StringComparison.OrdinalIgnoreCase);

    private static int Priority(RepositoryBriefingRole roles)
    {
        if (roles.HasFlag(RepositoryBriefingRole.Guidance))
        {
            return GuidancePriority;
        }

        if (roles.HasFlag(RepositoryBriefingRole.BuildManifest))
        {
            return BuildManifestPriority;
        }

        if (roles.HasFlag(RepositoryBriefingRole.Seed))
        {
            return SeedPriority;
        }

        if (roles.HasFlag(RepositoryBriefingRole.Changed))
        {
            return ChangedPriority;
        }

        return roles.HasFlag(RepositoryBriefingRole.RepositoryContent) ? RepositoryContentPriority : ExcludedPriority;
    }

    private static string BuildSummary(RepositoryManifest manifest, IReadOnlyList<RepositoryBriefingEntry> entries, int unmatchedSeedCount) =>
        $"Repository manifest contains {manifest.EntryCount} paths: {manifest.ReviewableCount} reviewable, {manifest.ExcludedCount} excluded, {manifest.SelectedCount} changed or selected, "
        + $"{entries.Count(entry => entry.Roles.HasFlag(RepositoryBriefingRole.Seed))} seed matches, and {unmatchedSeedCount} unmatched seeds";

    private static string BuildDirectorySummary(IReadOnlyList<RepositoryBriefingEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (IGrouping<string, RepositoryBriefingEntry> group in entries.GroupBy(entry => TopLevelDirectory(entry.ManifestEntry.Path)).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.Append(group.Key.Length == 0 ? "(root)" : group.Key + '/');
            builder.Append(": ");
            builder.Append(group.Count());
            builder.Append(" paths, ");
            builder.Append(group.Count(entry => entry.ManifestEntry.Reviewable));
            builder.Append(" reviewable, ");
            builder.Append(group.Count(entry => entry.ManifestEntry.ChangeKind is not null));
            builder.Append(" selected, ");
            builder.Append(group.Count(entry => !entry.ManifestEntry.Reviewable));
            builder.AppendLine(" excluded");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<RepositoryManifestPartition> BuildPartitions(IReadOnlyList<RepositoryBriefingEntry> entries, int characterLimit)
    {
        var partitions = new List<RepositoryManifestPartition>();
        var lines = new List<string>();
        var paths = new List<string>();
        int characters = 0;

        foreach (RepositoryBriefingEntry entry in entries)
        {
            string line = FormatManifestLine(entry);
            int addedCharacters = line.Length + (lines.Count == 0 ? 0 : 1);
            if (lines.Count > 0 && characters + addedCharacters > characterLimit)
            {
                AddPartition(partitions, lines, paths, characters <= characterLimit);
                lines.Clear();
                paths.Clear();
                characters = 0;
                addedCharacters = line.Length;
            }

            lines.Add(line);
            paths.Add(entry.ManifestEntry.Path);
            characters += addedCharacters;

            if (line.Length > characterLimit)
            {
                AddPartition(partitions, lines, paths, withinCharacterLimit: false);
                lines.Clear();
                paths.Clear();
                characters = 0;
            }
        }

        if (lines.Count > 0)
        {
            AddPartition(partitions, lines, paths, withinCharacterLimit: true);
        }

        return partitions;
    }

    private static void AddPartition(List<RepositoryManifestPartition> partitions, List<string> lines, List<string> paths, bool withinCharacterLimit) =>
        partitions.Add(new RepositoryManifestPartition(partitions.Count + 1, string.Join('\n', lines), [.. paths], withinCharacterLimit));

    private static string FormatManifestLine(RepositoryBriefingEntry entry)
    {
        RepositoryManifestEntry manifestEntry = entry.ManifestEntry;
        string size = manifestEntry.SizeBytes?.ToString() ?? "unknown";
        string lines = manifestEntry.LineCount?.ToString() ?? "unknown";
        return $"{entry.Priority:D2} {manifestEntry.Path} | disposition={manifestEntry.Disposition} | bytes={size} | lines={lines} | roles={entry.Roles}";
    }

    private static string TopLevelDirectory(string path)
    {
        int separator = path.IndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private sealed record SeedPattern(string Pattern, Regex? Glob)
    {
        public static SeedPattern Create(string pattern)
        {
            string normalized = RepositoryRelativePath.Normalize(pattern);
            return new SeedPattern(normalized, ContainsGlob(normalized) ? CompileGlob(normalized) : null);
        }

        public bool IsMatch(string path) => Glob is not null ? Glob.IsMatch(path) : string.Equals(path, Pattern, Comparison) || path.StartsWith(Pattern + '/', Comparison);

        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static bool ContainsGlob(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

        private static Regex CompileGlob(string pattern)
        {
            var expression = new StringBuilder("^");
            for (int index = 0; index < pattern.Length; index++)
            {
                char current = pattern[index];
                if (current == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    bool followedBySlash = index + 2 < pattern.Length && pattern[index + 2] == '/';
                    expression.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    index += followedBySlash ? 2 : 1;
                }
                else if (current == '*')
                {
                    expression.Append("[^/]*");
                }
                else if (current == '?')
                {
                    expression.Append("[^/]");
                }
                else
                {
                    expression.Append(Regex.Escape(current.ToString()));
                }
            }

            expression.Append('$');
            RegexOptions options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
            return new Regex(expression.ToString(), options, TimeSpan.FromSeconds(1));
        }
    }
}
