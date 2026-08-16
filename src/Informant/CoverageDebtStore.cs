using System.Text.Json;

namespace Informant;

/// <summary>One adaptively deferred path retained for later deep review</summary>
public sealed record CoverageDebtEntry(string RepositoryUrl, string Branch, string Path, string GitObjectId, DateTimeOffset FirstDeferredUtc, DateTimeOffset LastDeferredUtc, string Reason,
    int Attempts);

/// <summary>Persistent-debt selection details for one review run</summary>
public sealed record CoverageDebtSelection(int PriorCount, IReadOnlyList<CoverageDebtEntry> Carried, int DiscardedStaleCount);

/// <summary>Stores metadata-only adaptive coverage debt outside the reviewed working tree</summary>
public sealed class CoverageDebtStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a store at the configured app-local path</summary>
    public CoverageDebtStore(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Selects oldest still-current debt without repeating files changed in this run</summary>
    public CoverageDebtSelection Select(string repositoryUrl, string branch, RepositoryManifest manifest, IReadOnlyCollection<string> currentPaths, int maxCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentPaths);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        List<CoverageDebtEntry> all = Load();
        List<CoverageDebtEntry> repositoryEntries = [.. all.Where(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch)];
        Dictionary<string, RepositoryManifestEntry> manifestByPath = manifest.Entries.ToDictionary(entry => entry.Path, comparer);
        var current = new HashSet<string>(currentPaths, comparer);
        CoverageDebtEntry[] valid =
        [
            .. repositoryEntries.Where(entry => IsCurrent(entry, manifestByPath))
        ];
        int staleCount = repositoryEntries.Count - valid.Length;
        if (staleCount > 0)
        {
            all.RemoveAll(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch && !valid.Contains(entry));
            Save(all);
        }

        CoverageDebtEntry[] carried =
        [
            .. valid
                .Where(entry => !current.Contains(entry.Path))
                .OrderBy(entry => entry.FirstDeferredUtc)
                .ThenByDescending(entry => entry.Attempts)
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .Take(maxCount)
        ];
        return new CoverageDebtSelection(valid.Length, carried, staleCount);
    }

    /// <summary>Reconciles persistent debt with the completed run and returns the remaining entry count for this repository and branch</summary>
    public int Update(string repositoryUrl, string branch, RepositoryManifest manifest, ReviewCoverageLedger coverage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(coverage);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        List<CoverageDebtEntry> all = Load();
        Dictionary<string, RepositoryManifestEntry> manifestByPath = manifest.Entries.ToDictionary(entry => entry.Path, comparer);
        var repositoryEntries = all
            .Where(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch && IsCurrent(entry, manifestByPath))
            .ToDictionary(entry => entry.Path, comparer);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        foreach (ReviewCoverageEntry entry in coverage.Entries)
        {
            if (!manifestByPath.TryGetValue(entry.Path, out RepositoryManifestEntry? manifestEntry) || string.IsNullOrWhiteSpace(manifestEntry.GitObjectId))
            {
                repositoryEntries.Remove(entry.Path);
                continue;
            }

            if (entry.DeepReviewDeferred && entry.Outcome is ReviewCoverageOutcome.Deferred or ReviewCoverageOutcome.MandatoryChangesReviewed)
            {
                if (repositoryEntries.TryGetValue(entry.Path, out CoverageDebtEntry? existing))
                {
                    repositoryEntries[entry.Path] = existing with { LastDeferredUtc = now, Reason = entry.Reason ?? existing.Reason, Attempts = existing.Attempts + 1 };
                }
                else
                {
                    repositoryEntries[entry.Path] = new CoverageDebtEntry(repositoryUrl, branch, entry.Path, manifestEntry.GitObjectId, now, now, entry.Reason ?? "adaptive deep review deferred", 1);
                }
            }
            else if (entry.Outcome is ReviewCoverageOutcome.DeepReviewed or ReviewCoverageOutcome.Excluded)
            {
                repositoryEntries.Remove(entry.Path);
            }
        }

        all.RemoveAll(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch);
        all.AddRange(repositoryEntries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal));
        Save(all);
        return repositoryEntries.Count;
    }

    private List<CoverageDebtEntry> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CoverageDebtEntry>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InformantFatalException($"Coverage-debt state could not be read from {_path}: {ex.Message}", ex);
        }
    }

    private void Save(IReadOnlyList<CoverageDebtEntry> entries)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsCurrent(CoverageDebtEntry entry, IReadOnlyDictionary<string, RepositoryManifestEntry> manifestByPath) => manifestByPath.TryGetValue(entry.Path, out RepositoryManifestEntry? current)
        && current.Reviewable
        && string.Equals(current.GitObjectId, entry.GitObjectId, StringComparison.Ordinal);
}
