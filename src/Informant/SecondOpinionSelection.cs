namespace Informant;

/// <summary>One primary result selected for second-opinion validation</summary>
public sealed record SecondOpinionSelectionItem(FileReviewResult Result, bool HasCandidates, bool CleanSample, bool SkippedPrimaryReview);

/// <summary>Selects bounded second-opinion work deterministically without hiding skipped primary results</summary>
public static class SecondOpinionSelection
{
    /// <summary>Builds the ordered validator queue for the configured scope</summary>
    public static IReadOnlyList<SecondOpinionSelectionItem> Build(IReadOnlyList<FileReviewResult> results, SecondOpinionConfig config)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(config);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        FileReviewResult[] candidates =
        [
            .. results
                .Where(result => result.StructuredCandidates.Count > 0)
                .OrderByDescending(result => result.CandidateSeverity)
                .ThenBy(result => result.File.Path, StringComparer.Ordinal)
        ];
        FileReviewResult[] reviewed = [.. results.Where(result => result.Findings is not null)];
        IEnumerable<FileReviewResult> scoped = config.Scope switch
        {
            SecondOpinionScope.AllReviewed => reviewed,
            SecondOpinionScope.CandidateOnly => candidates,
            SecondOpinionScope.CandidatePlusSample => candidates.Concat(SelectCleanSample(reviewed, config.MaxCleanFiles)),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Scope, "The second-opinion scope is not supported.")
        };

        var selected = new List<SecondOpinionSelectionItem>();
        var paths = new HashSet<string>(comparer);
        foreach (FileReviewResult result in scoped)
        {
            if (paths.Add(result.File.Path))
            {
                bool hasCandidates = result.StructuredCandidates.Count > 0;
                selected.Add(new SecondOpinionSelectionItem(result, hasCandidates, !hasCandidates && config.Scope == SecondOpinionScope.CandidatePlusSample, false));
            }
        }

        if (config.ReviewSkippedFiles)
        {
            foreach (FileReviewResult result in results.Where(IsSkippedPrimaryReview).OrderBy(result => result.File.Path, StringComparer.Ordinal))
            {
                if (paths.Add(result.File.Path))
                {
                    selected.Add(new SecondOpinionSelectionItem(result, false, false, true));
                }
            }
        }

        return selected;
    }

    private static IEnumerable<FileReviewResult> SelectCleanSample(IEnumerable<FileReviewResult> reviewed, int maxCleanFiles) => reviewed
        .Where(result => result.StructuredCandidates.Count == 0)
        .OrderBy(result => ChangeRisk(result.File.Kind))
        .ThenBy(result => result.File.Path, StringComparer.Ordinal)
        .Take(maxCleanFiles);

    private static bool IsSkippedPrimaryReview(FileReviewResult result) => result.Status != FileReviewStatus.Deferred && result.Findings is null && result.SkipReason is not null;

    private static int ChangeRisk(ChangeKind kind) => kind switch
    {
        ChangeKind.Modified => 0,
        ChangeKind.Renamed => 1,
        ChangeKind.Added => 2,
        ChangeKind.Deleted => 3,
        ChangeKind.FullReview => 4,
        _ => 5
    };
}
