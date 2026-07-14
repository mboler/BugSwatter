using System.Text.Json;
using BugSwatter.Common;

namespace Informant;

/// <summary>Versioned JSON planning contract presented to a model before clustered review</summary>
public static class RepositoryPlanningContract
{
    /// <summary>Current supported planning contract version</summary>
    public const int Version = 1;

    /// <summary>Compact instructions defining the required model response shape</summary>
    public static string Instructions => """
        Return JSON only with this shape:
        {
          "version": 1,
          "repositorySummary": "brief code-agnostic summary",
          "units": [
            {
              "id": "stable-unit-id",
              "priority": 1,
              "rationale": "why these paths belong together",
              "paths": ["exact/manifest/path"],
              "supportingPaths": ["optional/exact/manifest/path"]
            }
          ],
          "deferred": [ { "path": "exact/manifest/path", "reason": "adaptive-only reason" } ],
          "uncertainties": ["bounded uncertainty"]
        }
        Use only exact paths shown in the manifest. Do not invent paths, use absolute paths, or use parent traversal.
        Every changed or otherwise mandatory path must be assigned to one unit. Supporting paths may be shared by units.
        """;
}

/// <summary>Limits applied to untrusted model planning output</summary>
public sealed record RepositoryPlanLimits(int MaxPlanCharacters = 1_048_576, int MaxUnits = 200, int MaxPathsPerUnit = 100, int MaxSupportingPathsPerUnit = 50,
    int MaxDeferrals = 10_000, int MaxUncertainties = 100, int MaxTextCharacters = 8_000);

/// <summary>One review unit proposed by the model</summary>
public sealed record ProposedReviewUnit(string Id, int Priority, string Rationale, IReadOnlyList<string> Paths, IReadOnlyList<string>? SupportingPaths = null);

/// <summary>One path the model proposes to defer during adaptive review</summary>
public sealed record ProposedReviewDeferral(string Path, string Reason);

/// <summary>Untrusted versioned review plan returned by a model</summary>
public sealed record ProposedRepositoryReviewPlan(int Version, string RepositorySummary, IReadOnlyList<ProposedReviewUnit> Units, IReadOnlyList<ProposedReviewDeferral>? Deferred = null,
    IReadOnlyList<string>? Uncertainties = null);

/// <summary>One validated review unit using canonical manifest paths</summary>
public sealed record RepositoryReviewUnit(string Id, int Priority, string Rationale, IReadOnlyList<string> Paths, IReadOnlyList<string> SupportingPaths, bool ChangedLinesOnly = false);

/// <summary>One validated adaptive deferral using a canonical manifest path</summary>
public sealed record RepositoryReviewDeferral(string Path, string Reason);

/// <summary>Validated controller plan with explicit fallback and coverage-repair state</summary>
public sealed record RepositoryReviewPlan(string RepositorySummary, IReadOnlyList<RepositoryReviewUnit> Units, IReadOnlyList<RepositoryReviewDeferral> Deferred, IReadOnlyList<string> Uncertainties,
    bool UsedFallback, bool CoverageRepaired, IReadOnlyList<string> Diagnostics);

/// <summary>Adds mandatory changed-content units for adaptive paths whose full-file deep review was deferred</summary>
public static class RepositoryAdaptivePlan
{
    private const int MaxPathsPerMandatoryUnit = 50;

    /// <summary>Returns the original exhaustive plan or an adaptive plan augmented with bounded mandatory changed-content units</summary>
    public static RepositoryReviewPlan AddMandatoryChangedContent(RepositoryReviewPlan plan, IReadOnlyList<ChangedFile> files, ReviewStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(files);
        if (strategy == ReviewStrategy.Exhaustive || plan.Deferred.Count == 0)
        {
            return plan;
        }

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, ChangedFile> filesByPath = files.ToDictionary(file => file.Path, comparer);
        string[] mandatoryPaths =
        [
            .. plan.Deferred
                .Select(item => item.Path)
                .Where(path => filesByPath.TryGetValue(path, out ChangedFile? file) && file.Kind != ChangeKind.FullReview)
                .OrderBy(path => path, StringComparer.Ordinal)
        ];
        if (mandatoryPaths.Length == 0)
        {
            return plan;
        }

        var mandatoryUnits = new List<RepositoryReviewUnit>();
        foreach (IGrouping<string, string> group in mandatoryPaths.GroupBy(TopLevelDirectory, StringComparer.Ordinal))
        {
            string[] paths = [.. group];
            for (int index = 0; index < paths.Length; index += MaxPathsPerMandatoryUnit)
            {
                string[] unitPaths = paths[index..Math.Min(index + MaxPathsPerMandatoryUnit, paths.Length)];
                mandatoryUnits.Add(new RepositoryReviewUnit($"mandatory-changes-{mandatoryUnits.Count + 1:D3}", 95,
                    "Mandatory changed-line coverage for paths deferred from adaptive deep review", unitPaths, [], ChangedLinesOnly: true));
            }
        }

        return plan with
        {
            Units = [.. plan.Units, .. mandatoryUnits],
            Diagnostics = [.. plan.Diagnostics, $"controller added {mandatoryUnits.Count} units covering changed content in {mandatoryPaths.Length} adaptively deferred paths"]
        };
    }

    private static string TopLevelDirectory(string path)
    {
        int separator = path.IndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }
}

/// <summary>Parses untrusted model plans, validates all paths and bounds, and repairs coverage deterministically</summary>
public static class RepositoryReviewPlanValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Returns a validated plan, using a complete deterministic fallback when the model output is unusable</summary>
    public static RepositoryReviewPlan ValidateOrFallback(string? json, RepositoryManifest manifest, IReadOnlyCollection<string> candidatePaths, IReadOnlyCollection<string> mandatoryPaths,
        bool allowDeferrals, RepositoryPlanLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(candidatePaths);
        ArgumentNullException.ThrowIfNull(mandatoryPaths);

        RepositoryPlanLimits effectiveLimits = limits ?? new RepositoryPlanLimits();
        ValidateLimits(effectiveLimits);
        PlanningCatalog catalog = PlanningCatalog.Create(manifest, candidatePaths, mandatoryPaths);

        if (string.IsNullOrWhiteSpace(json))
        {
            return Fallback(catalog.CandidatePaths, effectiveLimits.MaxPathsPerUnit, "planning response was empty");
        }

        if (json.Length > effectiveLimits.MaxPlanCharacters)
        {
            return Fallback(catalog.CandidatePaths, effectiveLimits.MaxPathsPerUnit, $"planning response exceeded {effectiveLimits.MaxPlanCharacters} characters");
        }

        ProposedRepositoryReviewPlan? proposed;
        try
        {
            proposed = JsonSerializer.Deserialize<ProposedRepositoryReviewPlan>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fallback(catalog.CandidatePaths, effectiveLimits.MaxPathsPerUnit, $"planning response was not valid contract JSON: {ex.Message}");
        }

        if (proposed is null)
        {
            return Fallback(catalog.CandidatePaths, effectiveLimits.MaxPathsPerUnit, "planning response contained JSON null");
        }

        try
        {
            return Validate(proposed, catalog, allowDeferrals, effectiveLimits);
        }
        catch (PlanValidationException ex)
        {
            return Fallback(catalog.CandidatePaths, effectiveLimits.MaxPathsPerUnit, ex.Message);
        }
    }

    private static RepositoryReviewPlan Validate(ProposedRepositoryReviewPlan proposed, PlanningCatalog catalog, bool allowDeferrals, RepositoryPlanLimits limits)
    {
        if (proposed.Version != RepositoryPlanningContract.Version)
        {
            throw new PlanValidationException($"unsupported planning contract version {proposed.Version}");
        }

        ValidateText(proposed.RepositorySummary, nameof(proposed.RepositorySummary), limits.MaxTextCharacters);
        if (proposed.Units is null)
        {
            throw new PlanValidationException("planning response did not contain a review-units array");
        }

        if (proposed.Units.Count == 0 && (!allowDeferrals || proposed.Deferred is not { Count: > 0 }))
        {
            throw new PlanValidationException("planning response contained neither review units nor adaptive deferrals");
        }

        if (proposed.Units.Count > limits.MaxUnits)
        {
            throw new PlanValidationException($"planning response contained {proposed.Units.Count} units, exceeding the limit of {limits.MaxUnits}");
        }

        StringComparer comparer = catalog.Comparer;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var assigned = new HashSet<string>(comparer);
        var units = new List<RepositoryReviewUnit>(proposed.Units.Count);
        foreach (ProposedReviewUnit? proposedUnit in proposed.Units)
        {
            if (proposedUnit is null)
            {
                throw new PlanValidationException("planning response contained a null review unit");
            }

            ValidateText(proposedUnit.Id, "unit id", 100);
            ValidateText(proposedUnit.Rationale, $"unit '{proposedUnit.Id}' rationale", limits.MaxTextCharacters);
            if (!identifiers.Add(proposedUnit.Id))
            {
                throw new PlanValidationException($"planning response duplicated unit id '{proposedUnit.Id}'");
            }

            if (proposedUnit.Priority is < 1 or > 100)
            {
                throw new PlanValidationException($"unit '{proposedUnit.Id}' priority must be between 1 and 100");
            }

            if (proposedUnit.Paths is null || proposedUnit.Paths.Count == 0 || proposedUnit.Paths.Count > limits.MaxPathsPerUnit)
            {
                throw new PlanValidationException($"unit '{proposedUnit.Id}' must contain 1 to {limits.MaxPathsPerUnit} primary paths");
            }

            var paths = new List<string>(proposedUnit.Paths.Count);
            foreach (string path in proposedUnit.Paths)
            {
                string canonicalPath = catalog.RequireCandidate(path);
                if (!assigned.Add(canonicalPath))
                {
                    throw new PlanValidationException($"planning response assigned path '{canonicalPath}' more than once");
                }

                paths.Add(canonicalPath);
            }

            IReadOnlyList<string> proposedSupportingPaths = proposedUnit.SupportingPaths ?? [];
            if (proposedSupportingPaths.Count > limits.MaxSupportingPathsPerUnit)
            {
                throw new PlanValidationException($"unit '{proposedUnit.Id}' exceeded the supporting-path limit of {limits.MaxSupportingPathsPerUnit}");
            }

            string[] supportingPaths =
            [
                .. proposedSupportingPaths
                    .Select(catalog.RequireReviewable)
                    .Distinct(comparer)
                    .OrderBy(path => path, StringComparer.Ordinal)
            ];
            units.Add(new RepositoryReviewUnit(proposedUnit.Id, proposedUnit.Priority, proposedUnit.Rationale, paths, supportingPaths));
        }

        IReadOnlyList<ProposedReviewDeferral> proposedDeferrals = proposed.Deferred ?? [];
        if (proposedDeferrals.Count > limits.MaxDeferrals)
        {
            throw new PlanValidationException($"planning response exceeded the deferral limit of {limits.MaxDeferrals}");
        }

        if (!allowDeferrals && proposedDeferrals.Count > 0)
        {
            throw new PlanValidationException("exhaustive planning cannot defer candidate paths");
        }

        var deferredPaths = new HashSet<string>(comparer);
        var deferrals = new List<RepositoryReviewDeferral>();
        var diagnostics = new List<string>();
        foreach (ProposedReviewDeferral? proposedDeferral in proposedDeferrals)
        {
            if (proposedDeferral is null)
            {
                throw new PlanValidationException("planning response contained a null deferral");
            }

            ValidateText(proposedDeferral.Reason, $"deferral reason for '{proposedDeferral.Path}'", limits.MaxTextCharacters);
            string canonicalPath = catalog.RequireCandidate(proposedDeferral.Path);
            if (assigned.Contains(canonicalPath) || deferredPaths.Contains(canonicalPath))
            {
                throw new PlanValidationException($"planning response duplicated path '{canonicalPath}' across assignments or deferrals");
            }

            if (catalog.MandatoryPaths.Contains(canonicalPath))
            {
                diagnostics.Add($"mandatory path '{canonicalPath}' could not be deferred and was assigned by coverage repair");
                continue;
            }

            deferredPaths.Add(canonicalPath);
            deferrals.Add(new RepositoryReviewDeferral(canonicalPath, proposedDeferral.Reason));
        }

        string[] repairPaths = [.. catalog.CandidatePaths.Where(path => !assigned.Contains(path) && !deferredPaths.Contains(path)).OrderBy(path => path, StringComparer.Ordinal)];
        if (repairPaths.Length > 0)
        {
            diagnostics.Add($"coverage repair assigned {repairPaths.Length} candidate paths omitted by the model plan");
            units.AddRange(BuildFallbackUnits(repairPaths, limits.MaxPathsPerUnit, "coverage-repair", 90));
        }

        string[] uncertainties = ValidateUncertainties(proposed.Uncertainties ?? [], limits);
        return new RepositoryReviewPlan(proposed.RepositorySummary, units, deferrals, uncertainties, false, repairPaths.Length > 0 || diagnostics.Count > 0, diagnostics);
    }

    private static string[] ValidateUncertainties(IReadOnlyList<string> uncertainties, RepositoryPlanLimits limits)
    {
        if (uncertainties.Count > limits.MaxUncertainties)
        {
            throw new PlanValidationException($"planning response exceeded the uncertainty limit of {limits.MaxUncertainties}");
        }

        foreach (string uncertainty in uncertainties)
        {
            ValidateText(uncertainty, "uncertainty", limits.MaxTextCharacters);
        }

        return [.. uncertainties];
    }

    private static RepositoryReviewPlan Fallback(IReadOnlyList<string> candidatePaths, int maxPathsPerUnit, string reason)
    {
        IReadOnlyList<RepositoryReviewUnit> units = BuildFallbackUnits(candidatePaths, maxPathsPerUnit, "fallback", 100);
        return new RepositoryReviewPlan("Deterministic controller fallback after unusable model planning output", units, [], [], true, false, [reason]);
    }

    private static IReadOnlyList<RepositoryReviewUnit> BuildFallbackUnits(IEnumerable<string> paths, int maxPathsPerUnit, string identifierPrefix, int priority)
    {
        var units = new List<RepositoryReviewUnit>();
        foreach (IGrouping<string, string> group in paths.OrderBy(path => path, StringComparer.Ordinal).GroupBy(TopLevelDirectory, StringComparer.Ordinal))
        {
            string[] groupedPaths = [.. group];
            for (int index = 0; index < groupedPaths.Length; index += maxPathsPerUnit)
            {
                string[] unitPaths = groupedPaths[index..Math.Min(index + maxPathsPerUnit, groupedPaths.Length)];
                string location = group.Key.Length == 0 ? "repository root" : $"'{group.Key}'";
                units.Add(new RepositoryReviewUnit($"{identifierPrefix}-{units.Count + 1:D3}", priority, $"Deterministic path-proximity grouping for {location}", unitPaths, []));
            }
        }

        return units;
    }

    private static void ValidateLimits(RepositoryPlanLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxPlanCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxUnits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxPathsPerUnit);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxSupportingPathsPerUnit);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxDeferrals);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.MaxUncertainties);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxTextCharacters);
    }

    private static void ValidateText(string? value, string field, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PlanValidationException($"planning field {field} is required");
        }

        if (value.Length > maxCharacters)
        {
            throw new PlanValidationException($"planning field {field} exceeded {maxCharacters} characters");
        }
    }

    private static string TopLevelDirectory(string path)
    {
        int separator = path.IndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private sealed class PlanningCatalog
    {
        private readonly IReadOnlyDictionary<string, string> _reviewablePaths;
        private readonly IReadOnlySet<string> _candidateSet;

        private PlanningCatalog(StringComparer comparer, IReadOnlyDictionary<string, string> reviewablePaths, IReadOnlyList<string> candidatePaths, IReadOnlySet<string> candidateSet,
            IReadOnlySet<string> mandatoryPaths)
        {
            Comparer = comparer;
            _reviewablePaths = reviewablePaths;
            CandidatePaths = candidatePaths;
            _candidateSet = candidateSet;
            MandatoryPaths = mandatoryPaths;
        }

        public StringComparer Comparer { get; }

        public IReadOnlyList<string> CandidatePaths { get; }

        public IReadOnlySet<string> MandatoryPaths { get; }

        public static PlanningCatalog Create(RepositoryManifest manifest, IReadOnlyCollection<string> candidatePaths, IReadOnlyCollection<string> mandatoryPaths)
        {
            StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            Dictionary<string, string> reviewablePaths = manifest.Entries
                .Where(entry => entry.Reviewable)
                .ToDictionary(entry => entry.Path, entry => entry.Path, comparer);
            string[] canonicalCandidates = [.. candidatePaths.Select(path => RequireKnown(path, reviewablePaths, "candidate")).Distinct(comparer).OrderBy(path => path, StringComparer.Ordinal)];
            var candidateSet = new HashSet<string>(canonicalCandidates, comparer);
            string[] canonicalMandatory = [.. mandatoryPaths.Select(path => RequireKnown(path, reviewablePaths, "mandatory")).Distinct(comparer)];
            if (canonicalMandatory.Any(path => !candidateSet.Contains(path)))
            {
                throw new ArgumentException("Every mandatory path must also be a candidate path", nameof(mandatoryPaths));
            }

            return new PlanningCatalog(comparer, reviewablePaths, canonicalCandidates, candidateSet, new HashSet<string>(canonicalMandatory, comparer));
        }

        public string RequireCandidate(string path)
        {
            string canonical = RequireReviewable(path);
            if (!_candidateSet.Contains(canonical))
            {
                throw new PlanValidationException($"planning response referenced non-candidate path '{canonical}' as primary review work");
            }

            return canonical;
        }

        public string RequireReviewable(string path)
        {
            if (!RepositoryRelativePath.TryNormalize(path, out string normalized) || !_reviewablePaths.TryGetValue(normalized, out string? canonical))
            {
                throw new PlanValidationException($"planning response referenced unsafe, unknown, or excluded path '{path}'");
            }

            return canonical;
        }

        private static string RequireKnown(string path, IReadOnlyDictionary<string, string> knownPaths, string kind)
        {
            if (!RepositoryRelativePath.TryNormalize(path, out string normalized) || !knownPaths.TryGetValue(normalized, out string? canonical))
            {
                throw new ArgumentException($"{kind} path is absent from the reviewable manifest: '{path}'");
            }

            return canonical;
        }
    }

    private sealed class PlanValidationException : Exception
    {
        public PlanValidationException(string message) : base(message)
        {
        }
    }
}
