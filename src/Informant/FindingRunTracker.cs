using BugSwatter.Common;

namespace Informant;

/// <summary>Counts and artifact location reported after finding disposition completes</summary>
public sealed record FindingRunSummary(string ArtifactPath, int NewCount, int KnownCount, int SuppressedCount, int ConfirmedCount, int DiscardedCount, int UnresolvedCount, int IncompleteCount,
    int AcceptedLedgerCount, int InvalidSuppressionMarkerCount, int StaleSuppressionCount);

/// <summary>Builds structural identities, applies accepted and inline dispositions, and reconciles validator outcomes</summary>
public sealed class FindingRunTracker
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _repositoryUrl;
    private readonly string _branch;
    private readonly string _tipSha;
    private readonly RepositoryFileReader _fileReader;
    private readonly GitRunner _git;
    private readonly int _maxFileBytes;
    private readonly FindingStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _createdUtc;
    private readonly Dictionary<string, RepositoryManifestEntry> _manifestByPath;
    private readonly Dictionary<string, SourceContext> _sourceContexts = new(PathComparer);
    private readonly Dictionary<string, IReadOnlyList<bool>> _suppressionMasks = new(PathComparer);
    private readonly HashSet<string> _invalidMarkers = new(StringComparer.Ordinal);
    private readonly List<FindingArtifactEntry> _entries = [];

    private FindingStateSnapshot _stateSnapshot;

    private FindingRunTracker(string repositoryUrl, string branch, string tipSha, RepositoryManifest manifest, string treeRoot, int maxFileBytes, GitRunner git, FindingStateStore stateStore,
        TimeProvider timeProvider)
    {
        _repositoryUrl = repositoryUrl;
        _branch = branch;
        _tipSha = tipSha;
        _fileReader = new RepositoryFileReader(treeRoot, maxFileBytes);
        _git = git;
        _maxFileBytes = maxFileBytes;
        _stateStore = stateStore;
        _timeProvider = timeProvider;
        _createdUtc = timeProvider.GetUtcNow();
        _manifestByPath = manifest.Entries.ToDictionary(entry => RepositoryRelativePath.Normalize(entry.Path), PathComparer);
        _stateSnapshot = stateStore.LoadSnapshot(repositoryUrl, branch);
    }

    /// <summary>Creates a tracker and identifies every structured primary finding</summary>
    public static async Task<FindingRunTracker> CreateAsync(string repositoryUrl, string branch, string tipSha, IReadOnlyList<FileReviewResult> results, RepositoryManifest manifest,
        string treeRoot, int maxFileBytes, GitRunner git, string statePath, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(tipSha);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(git);

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        var tracker = new FindingRunTracker(repositoryUrl, branch, tipSha, manifest, treeRoot, maxFileBytes, git, new FindingStateStore(statePath, clock), clock);
        foreach (FileReviewResult result in results)
        {
            var suppressionMask = new List<bool>(result.StructuredCandidates.Count);
            foreach (CandidateFinding candidate in result.StructuredCandidates)
            {
                FindingArtifactEntry entry = await tracker.CreateEntryAsync(result.File.Path, candidate.File, candidate.Line, candidate.Category, candidate.Severity, candidate.Summary,
                    FindingOrigin.Primary, FindingValidatorStatus.NotRun, result.File.ChangedRanges, cancellationToken);
                tracker.AddOrUpdate(entry);
                suppressionMask.Add(entry.Disposition == FindingDisposition.Suppressed);
            }

            tracker._suppressionMasks[result.File.Path] = suppressionMask;
        }

        tracker.RefreshSuppressionState();
        return tracker;
    }

    /// <summary>Removes suppressed structured candidates from severity routing and candidate-scoped validation</summary>
    public IReadOnlyList<FileReviewResult> FilterSuppressedCandidates(IReadOnlyList<FileReviewResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var filtered = new List<FileReviewResult>(results.Count);
        foreach (FileReviewResult result in results)
        {
            if (!_suppressionMasks.TryGetValue(result.File.Path, out IReadOnlyList<bool>? mask) || mask.Count != result.StructuredCandidates.Count)
            {
                filtered.Add(result);
                continue;
            }

            CandidateFinding[] candidates = [.. result.StructuredCandidates.Where((_, index) => !mask[index])];
            Severity severity = candidates.Select(candidate => SecondOpinionParser.ParseSeverity(candidate.Severity)).DefaultIfEmpty(Severity.None).Max();
            filtered.Add(result with { CandidateSeverity = severity, CandidateFindings = candidates });
        }

        return filtered;
    }

    /// <summary>Applies per-file second-opinion outcomes and records unmatched confirmed findings</summary>
    public async Task ApplySecondOpinionAsync(bool configured, IReadOnlyList<SecondOpinionFileValidation>? validations, CancellationToken cancellationToken = default)
    {
        if (!configured)
        {
            return;
        }

        if (validations is null)
        {
            ReplacePrimaryStatuses(_ => FindingValidatorStatus.Incomplete);
            return;
        }

        var validationsByFile = new Dictionary<string, SecondOpinionFileValidation>(PathComparer);
        var fingerprintsByFile = new Dictionary<string, ValidatorFingerprintSet>(PathComparer);
        foreach (SecondOpinionFileValidation validation in validations)
        {
            validationsByFile[validation.File] = validation;
            if (validation.Status != SecondOpinionValidationStatus.Validated)
            {
                continue;
            }

            HashSet<string> confirmed = await CreateValidatorFingerprintsAsync(validation, validation.Confirmed.Select(finding => new ValidatorFinding(finding.File, finding.Line, finding.Category)),
                cancellationToken);
            HashSet<string> discarded = await CreateValidatorFingerprintsAsync(validation, validation.Discarded.Select(finding => new ValidatorFinding(finding.File, finding.Line, finding.Category)),
                cancellationToken);
            fingerprintsByFile[validation.File] = new ValidatorFingerprintSet(confirmed, discarded);
        }

        for (int index = 0; index < _entries.Count; index++)
        {
            FindingArtifactEntry entry = _entries[index];
            if (entry.Origin != FindingOrigin.Primary || entry.Disposition == FindingDisposition.Suppressed)
            {
                continue;
            }

            if (!validationsByFile.TryGetValue(entry.ReviewFile, out SecondOpinionFileValidation? validation) || validation.Status != SecondOpinionValidationStatus.Validated
                || !fingerprintsByFile.TryGetValue(entry.ReviewFile, out ValidatorFingerprintSet? fingerprints))
            {
                _entries[index] = entry with { ValidatorStatus = FindingValidatorStatus.Incomplete };
                continue;
            }

            FindingValidatorStatus status = fingerprints.Confirmed.Contains(entry.Fingerprint)
                ? FindingValidatorStatus.Confirmed
                : fingerprints.Discarded.Contains(entry.Fingerprint) ? FindingValidatorStatus.Discarded : FindingValidatorStatus.Unresolved;
            _entries[index] = entry with { ValidatorStatus = status };
        }

        foreach (SecondOpinionFileValidation validation in validations.Where(validation => validation.Status == SecondOpinionValidationStatus.Validated))
        {
            foreach (ConfirmedFinding finding in validation.Confirmed.Where(finding => finding.Line is not null))
            {
                FindingArtifactEntry entry = await CreateEntryAsync(validation.File, finding.File, finding.Line, finding.Category, finding.Severity, finding.Summary, FindingOrigin.SecondOpinion,
                    FindingValidatorStatus.Confirmed, [], cancellationToken);
                if (_entries.All(existing => existing.Fingerprint != entry.Fingerprint))
                {
                    _entries.Add(entry);
                }
            }
        }

        RefreshSuppressionState();
    }

    /// <summary>Writes or replaces the current run artifact and returns report-ready counts</summary>
    public FindingRunSummary WriteArtifact(string reportDirectory, string runStamp)
    {
        FindingRunArtifact artifact = CreateArtifact();
        string path = FindingArtifactFile.Write(reportDirectory, runStamp, artifact);
        return new FindingRunSummary(path, artifact.Findings.Count(finding => finding.Disposition == FindingDisposition.New),
            artifact.Findings.Count(finding => finding.Disposition == FindingDisposition.Known), artifact.Findings.Count(finding => finding.Disposition == FindingDisposition.Suppressed),
            artifact.Findings.Count(finding => finding.ValidatorStatus == FindingValidatorStatus.Confirmed),
            artifact.Findings.Count(finding => finding.ValidatorStatus == FindingValidatorStatus.Discarded),
            artifact.Findings.Count(finding => finding.ValidatorStatus == FindingValidatorStatus.Unresolved),
            artifact.Findings.Count(finding => finding.ValidatorStatus == FindingValidatorStatus.Incomplete), artifact.AcceptedLedgerCount, artifact.InvalidSuppressionMarkerCount,
            artifact.StaleSuppressionCount);
    }

    /// <summary>Creates an immutable snapshot of current finding dispositions and validator outcomes</summary>
    public FindingRunArtifact CreateArtifact()
    {
        DateTimeOffset staleBefore = _timeProvider.GetUtcNow().AddDays(-90);
        int staleSuppressions = _entries
            .Where(entry => entry.Disposition == FindingDisposition.Suppressed)
            .Select(entry => _stateSnapshot.FindSuppression(entry.Fingerprint))
            .Where(state => state?.FirstObservedUtc <= staleBefore)
            .Count();
        FindingArtifactEntry[] ordered =
        [
            .. _entries
                .OrderByDescending(entry => SecondOpinionParser.ParseSeverity(entry.Severity))
                .ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .ThenBy(entry => entry.AnchorLine)
                .ThenBy(entry => entry.Fingerprint, StringComparer.Ordinal)
        ];
        return new FindingRunArtifact(1, _repositoryUrl, _branch, _tipSha, _createdUtc, ordered, _stateSnapshot.Accepted.Count, _invalidMarkers.Count, staleSuppressions);
    }

    private async Task<FindingArtifactEntry> CreateEntryAsync(string reviewFile, string? reportedPath, int? reportedLine, string? category, string severity, string summary, FindingOrigin origin,
        FindingValidatorStatus validatorStatus, IReadOnlyList<LineRange> fallbackRanges, CancellationToken cancellationToken)
    {
        RepositoryManifestEntry manifestEntry = ResolveManifestEntry(reportedPath, reviewFile);
        SourceContext context = await GetSourceContextAsync(manifestEntry, cancellationToken);
        int fallbackLine = fallbackRanges.Count > 0 ? fallbackRanges[0].Start : 1;
        FindingIdentity identity = FindingFingerprint.Create(manifestEntry.Path, category, context.Lines, reportedLine ?? fallbackLine);
        FindingSuppressionMarker? marker = context.Suppressions.Markers.FirstOrDefault(candidate => candidate.AppliesTo(identity.AnchorLine));
        AcceptedFindingState? accepted = _stateSnapshot.FindAccepted(identity.Fingerprint);
        FindingDisposition disposition = marker is not null ? FindingDisposition.Suppressed : accepted is not null ? FindingDisposition.Known : FindingDisposition.New;
        return new FindingArtifactEntry(identity.Fingerprint, identity.Version, origin, reviewFile, identity.Path, identity.ReportedLine, identity.AnchorLine, identity.Category, severity, summary,
            identity.PreviousAnchor, identity.SourceAnchor, identity.NextAnchor, disposition, validatorStatus, accepted?.AcceptedAtUtc, marker?.Justification, marker?.MarkerLine);
    }

    private async Task<HashSet<string>> CreateValidatorFingerprintsAsync(SecondOpinionFileValidation validation, IEnumerable<ValidatorFinding> findings, CancellationToken cancellationToken)
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (ValidatorFinding finding in findings.Where(finding => finding.Line is not null))
        {
            RepositoryManifestEntry entry = ResolveManifestEntry(finding.File, validation.File);
            SourceContext context = await GetSourceContextAsync(entry, cancellationToken);
            fingerprints.Add(FindingFingerprint.Create(entry.Path, finding.Category, context.Lines, finding.Line).Fingerprint);
        }

        return fingerprints;
    }

    private RepositoryManifestEntry ResolveManifestEntry(string? reportedPath, string fallbackPath)
    {
        if (TryGetManifestEntry(reportedPath, out RepositoryManifestEntry? reported))
        {
            return reported!;
        }

        if (TryGetManifestEntry(fallbackPath, out RepositoryManifestEntry? fallback))
        {
            return fallback!;
        }

        throw new InformantFatalException($"A structured finding could not be attributed to reviewable manifest path '{fallbackPath}'.");
    }

    private bool TryGetManifestEntry(string? path, out RepositoryManifestEntry? entry)
    {
        entry = null;
        return RepositoryRelativePath.TryNormalize(path, out string normalized) && _manifestByPath.TryGetValue(normalized, out entry) && entry.Reviewable;
    }

    private async Task<SourceContext> GetSourceContextAsync(RepositoryManifestEntry entry, CancellationToken cancellationToken)
    {
        if (_sourceContexts.TryGetValue(entry.Path, out SourceContext? cached))
        {
            return cached;
        }

        string[] lines = entry.Disposition == RepositoryManifestDisposition.DeletedFromTip
            ? await ReadDeletedLinesAsync(entry, cancellationToken)
            : await _fileReader.ReadAllLinesAsync(entry.Path, cancellationToken);

        FindingSuppressionScan suppressions = FindingFingerprint.ScanSuppressions(lines);
        foreach (int line in suppressions.InvalidMarkerLines)
        {
            _invalidMarkers.Add($"{entry.Path}:{line}");
        }

        var context = new SourceContext(lines, suppressions);
        _sourceContexts[entry.Path] = context;
        return context;
    }

    private async Task<string[]> ReadDeletedLinesAsync(RepositoryManifestEntry entry, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ContentRevision))
        {
            throw new RepositoryFileException(RepositoryFileError.ReadFailed, $"Deleted file '{entry.Path}' has no baseline Git revision available.");
        }

        return await GitBlobReader.ReadLinesAsync(_git, _fileReader.Root, entry.ContentRevision, entry.Path, _maxFileBytes, cancellationToken);
    }

    private void AddOrUpdate(FindingArtifactEntry entry)
    {
        int existingIndex = _entries.FindIndex(existing => existing.Fingerprint == entry.Fingerprint);
        if (existingIndex < 0)
        {
            _entries.Add(entry);
            return;
        }

        FindingArtifactEntry existing = _entries[existingIndex];
        if (SecondOpinionParser.ParseSeverity(entry.Severity) > SecondOpinionParser.ParseSeverity(existing.Severity))
        {
            _entries[existingIndex] = entry;
        }
    }

    private void RefreshSuppressionState()
    {
        _stateSnapshot = _stateStore.ObserveSuppressions(_repositoryUrl, _branch, _entries.Where(entry => entry.Disposition == FindingDisposition.Suppressed));
    }

    private void ReplacePrimaryStatuses(Func<FindingArtifactEntry, FindingValidatorStatus> status)
    {
        for (int index = 0; index < _entries.Count; index++)
        {
            if (_entries[index].Origin == FindingOrigin.Primary && _entries[index].Disposition != FindingDisposition.Suppressed)
            {
                _entries[index] = _entries[index] with { ValidatorStatus = status(_entries[index]) };
            }
        }
    }

    private sealed record SourceContext(string[] Lines, FindingSuppressionScan Suppressions);

    private sealed record ValidatorFinding(string? File, int? Line, string Category);

    private sealed record ValidatorFingerprintSet(HashSet<string> Confirmed, HashSet<string> Discarded);
}
