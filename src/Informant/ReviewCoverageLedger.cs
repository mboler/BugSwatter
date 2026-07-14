using System.Text.Json;
using System.Text.Json.Serialization;

namespace Informant;

/// <summary>Honest final coverage outcome for one candidate path</summary>
public enum ReviewCoverageOutcome
{
    /// <summary>The selected source was deeply reviewed</summary>
    DeepReviewed,

    /// <summary>Deep review was deferred but mandatory changed content was reviewed</summary>
    MandatoryChangesReviewed,

    /// <summary>The path was intentionally deferred without a mandatory changed-content requirement</summary>
    Deferred,

    /// <summary>The path was deterministically excluded from model review</summary>
    Excluded,

    /// <summary>No required review completed</summary>
    Failed,

    /// <summary>Only part of the required review completed</summary>
    Partial
}

/// <summary>Coverage facts for one candidate path without source or model-response content</summary>
public sealed record ReviewCoverageEntry(string Path, ChangeKind ChangeKind, bool SelectedForDeepReview, bool DeepReviewDeferred, bool MandatoryChangedContent,
    ReviewCoverageOutcome Outcome, string? Reason);

/// <summary>Run-level coverage ledger used for reporting and strategy-aware baseline advancement</summary>
public sealed record ReviewCoverageLedger(ReviewStrategy Strategy, IReadOnlyList<ReviewCoverageEntry> Entries)
{
    /// <summary>Number of paths selected for deep review</summary>
    public int SelectedCount => Entries.Count(entry => entry.SelectedForDeepReview);

    /// <summary>Number of paths deeply reviewed</summary>
    public int DeepReviewedCount => Entries.Count(entry => entry.Outcome == ReviewCoverageOutcome.DeepReviewed);

    /// <summary>Number of paths whose mandatory changed content was reviewed after deep review was deferred</summary>
    public int MandatoryChangesReviewedCount => Entries.Count(entry => entry.Outcome == ReviewCoverageOutcome.MandatoryChangesReviewed);

    /// <summary>Number of paths intentionally deferred without mandatory changed content</summary>
    public int DeferredCount => Entries.Count(entry => entry.Outcome == ReviewCoverageOutcome.Deferred);

    /// <summary>Number of paths deterministically excluded</summary>
    public int ExcludedCount => Entries.Count(entry => entry.Outcome == ReviewCoverageOutcome.Excluded);

    /// <summary>Number of paths with failed required review</summary>
    public int FailedCount => Entries.Count(entry => entry.Outcome == ReviewCoverageOutcome.Failed);

    /// <summary>Number of paths with partial required review</summary>
    public int PartialCount => Entries.Count(entry => entry.Outcome == ReviewCoverageOutcome.Partial);

    /// <summary>Whether all mandatory work completed under the configured strategy</summary>
    public bool CanAdvanceBaseline => Entries.All(entry => entry.Outcome is not ReviewCoverageOutcome.Failed and not ReviewCoverageOutcome.Partial
        && (!entry.MandatoryChangedContent || entry.Outcome is ReviewCoverageOutcome.DeepReviewed or ReviewCoverageOutcome.MandatoryChangesReviewed or ReviewCoverageOutcome.Excluded));

    /// <summary>Builds an ordered ledger from the validated plan and aggregate file results</summary>
    public static ReviewCoverageLedger Create(ReviewStrategy strategy, IReadOnlyList<ChangedFile> files, RepositoryReviewPlan plan, IReadOnlyList<FileReviewResult> results)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(results);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var deepSelected = new HashSet<string>(plan.Units.Where(unit => !unit.ChangedLinesOnly).SelectMany(unit => unit.Paths), comparer);
        var mandatorySelected = new HashSet<string>(plan.Units.Where(unit => unit.ChangedLinesOnly).SelectMany(unit => unit.Paths), comparer);
        var deferred = plan.Deferred.ToDictionary(item => item.Path, item => item.Reason, comparer);
        Dictionary<string, FileReviewResult> resultsByPath = results.ToDictionary(result => result.File.Path, comparer);
        var entries = new List<ReviewCoverageEntry>(files.Count);
        foreach (ChangedFile file in files)
        {
            if (!resultsByPath.TryGetValue(file.Path, out FileReviewResult? result))
            {
                entries.Add(new ReviewCoverageEntry(file.Path, file.Kind, deepSelected.Contains(file.Path), deferred.ContainsKey(file.Path), mandatorySelected.Contains(file.Path),
                    ReviewCoverageOutcome.Failed, "aggregate review result was missing"));
                continue;
            }

            ReviewCoverageOutcome outcome = Outcome(result, deepSelected.Contains(file.Path), mandatorySelected.Contains(file.Path));
            string? reason = result.SkipReason ?? deferred.GetValueOrDefault(file.Path);
            entries.Add(new ReviewCoverageEntry(file.Path, file.Kind, deepSelected.Contains(file.Path), deferred.ContainsKey(file.Path), mandatorySelected.Contains(file.Path), outcome, reason));
        }

        return new ReviewCoverageLedger(strategy, entries);
    }

    private static ReviewCoverageOutcome Outcome(FileReviewResult result, bool deepSelected, bool mandatorySelected) => result.Status switch
    {
        FileReviewStatus.Reviewed when deepSelected => ReviewCoverageOutcome.DeepReviewed,
        FileReviewStatus.Reviewed when mandatorySelected => ReviewCoverageOutcome.MandatoryChangesReviewed,
        FileReviewStatus.Deferred when mandatorySelected && result.CompletedChunks == result.TotalChunks && result.TotalChunks > 0 => ReviewCoverageOutcome.MandatoryChangesReviewed,
        FileReviewStatus.Deferred => ReviewCoverageOutcome.Deferred,
        FileReviewStatus.NotReviewable => ReviewCoverageOutcome.Excluded,
        FileReviewStatus.Partial => ReviewCoverageOutcome.Partial,
        _ => ReviewCoverageOutcome.Failed
    };
}

/// <summary>Writes the metadata-only coverage ledger as a timestamped JSON artifact</summary>
public static class ReviewCoverageLedgerFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Writes the coverage ledger and returns the artifact path</summary>
    public static string Write(string reportDirectory, string runStamp, ReviewCoverageLedger ledger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runStamp);
        ArgumentNullException.ThrowIfNull(ledger);

        Directory.CreateDirectory(reportDirectory);
        string path = Path.Combine(reportDirectory, $"Informant-Coverage-{runStamp}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(ledger, JsonOptions));
        return path;
    }
}
