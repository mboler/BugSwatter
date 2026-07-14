using System.Text;

namespace Informant;

/// <summary>Aggregate outcome of bounded repository planning batches</summary>
public sealed record RepositoryPlanningResult(RepositoryReviewPlan Plan, int BatchCount, int ModelBatchCount);

/// <summary>Plans bounded candidate batches and combines validated results without allowing planning output to reduce required coverage</summary>
public sealed class RepositoryReviewPlanner
{
    private const int InitialContextPercent = 55;

    private static readonly string SystemPrompt = $"""
        You organize a code-agnostic repository review into coherent bounded units. Planning never grants tools or repository access.

        {RepositoryPlanningContract.Instructions}
        """;

    private readonly int _maxContextCharacters;

    /// <summary>Creates a planner using the configured model conversation budget</summary>
    public RepositoryReviewPlanner(int maxContextCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContextCharacters);
        _maxContextCharacters = maxContextCharacters;
    }

    /// <summary>Plans every candidate partition and deterministically falls back for invalid or oversized model output</summary>
    public async Task<RepositoryPlanningResult> PlanAsync(RepositoryManifest manifest, RepositoryBriefing briefing, IReadOnlyCollection<string> candidatePaths,
        IReadOnlyCollection<string> mandatoryPaths, bool allowDeferrals, Func<string, string, CancellationToken, Task<string>> modelCall, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(candidatePaths);
        ArgumentNullException.ThrowIfNull(mandatoryPaths);
        ArgumentNullException.ThrowIfNull(modelCall);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = new HashSet<string>(candidatePaths, comparer);
        var mandatory = new HashSet<string>(mandatoryPaths, comparer);
        var units = new List<RepositoryReviewUnit>();
        var deferred = new List<RepositoryReviewDeferral>();
        var uncertainties = new List<string>();
        var diagnostics = new List<string>();
        int batchCount = 0;
        int modelBatchCount = 0;
        bool usedFallback = false;
        bool coverageRepaired = false;

        foreach (RepositoryManifestPartition partition in briefing.ManifestPartitions)
        {
            string[] batchCandidates = [.. partition.Paths.Where(candidates.Contains)];
            if (batchCandidates.Length == 0)
            {
                continue;
            }

            batchCount++;
            string[] batchMandatory = [.. batchCandidates.Where(mandatory.Contains)];
            string? userPrompt = partition.WithinCharacterLimit ? BuildUserPrompt(briefing, partition, batchCandidates, allowDeferrals) : null;
            string? response = null;
            if (userPrompt is not null)
            {
                response = await modelCall(SystemPrompt, userPrompt, cancellationToken);
                modelBatchCount++;
            }

            RepositoryReviewPlan batchPlan = RepositoryReviewPlanValidator.ValidateOrFallback(response, manifest, batchCandidates, batchMandatory, allowDeferrals);
            string prefix = $"batch-{batchCount:D3}";
            units.AddRange(batchPlan.Units.Select(unit => unit with { Id = $"{prefix}-{unit.Id}" }));
            deferred.AddRange(batchPlan.Deferred);
            uncertainties.AddRange(batchPlan.Uncertainties.Select(uncertainty => $"{prefix}: {uncertainty}"));
            diagnostics.AddRange(batchPlan.Diagnostics.Select(diagnostic => $"{prefix}: {diagnostic}"));
            if (userPrompt is null)
            {
                diagnostics.Add($"{prefix}: manifest partition exceeded the bounded planning input and used deterministic grouping");
            }

            usedFallback |= batchPlan.UsedFallback;
            coverageRepaired |= batchPlan.CoverageRepaired;
        }

        var represented = new HashSet<string>(units.SelectMany(unit => unit.Paths).Concat(deferred.Select(item => item.Path)), comparer);
        string[] missing = [.. candidates.Where(path => !represented.Contains(path)).OrderBy(path => path, StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            RepositoryReviewPlan repair = RepositoryReviewPlanValidator.ValidateOrFallback(null, manifest, missing, missing.Where(mandatory.Contains).ToArray(), allowDeferrals: false);
            units.AddRange(repair.Units.Select(unit => unit with { Id = $"coverage-{unit.Id}" }));
            diagnostics.Add($"controller coverage repair assigned {missing.Length} candidates absent from manifest planning partitions");
            usedFallback = true;
            coverageRepaired = true;
        }

        var combined = new RepositoryReviewPlan(briefing.Summary, units, deferred, uncertainties, usedFallback, coverageRepaired, diagnostics);
        return new RepositoryPlanningResult(combined, batchCount, modelBatchCount);
    }

    private string? BuildUserPrompt(RepositoryBriefing briefing, RepositoryManifestPartition partition, IReadOnlyList<string> batchCandidates, bool allowDeferrals)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Plan this bounded repository manifest partition.");
        builder.AppendLine($"Strategy: {(allowDeferrals ? "adaptive; non-mandatory candidates may be deferred" : "exhaustive; no candidate may be deferred")}");
        builder.AppendLine($"Repository summary: {briefing.Summary}");
        builder.AppendLine("Candidate paths in this batch:");
        foreach (string path in batchCandidates)
        {
            builder.AppendLine(path);
        }

        builder.AppendLine();
        builder.AppendLine("Directory summary:");
        builder.AppendLine(briefing.DirectorySummary);
        builder.AppendLine();
        builder.AppendLine($"Manifest partition {partition.Number}:");
        builder.AppendLine(partition.Text);
        builder.AppendLine();
        builder.AppendLine(RepositoryPlanningContract.Instructions);

        string prompt = builder.ToString();
        return SystemPrompt.Length + prompt.Length <= _maxContextCharacters * InitialContextPercent / 100 ? prompt : null;
    }
}
