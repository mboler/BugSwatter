using System.Text;

namespace Informant;

/// <summary>One contiguous source part supplied to a clustered model conversation</summary>
public sealed record ReviewUnitPart(string Id, ChangedFile File, int PartNumber, int TotalParts, int StartLine, int EndLine, string SourceBlock, int ContentCharacters,
    bool MandatoryChangedContent = false);

/// <summary>One bounded sequential model conversation containing one or more related source parts</summary>
public sealed record ReviewExecutionUnit(string Id, string Rationale, IReadOnlyList<string> SupportingPaths, IReadOnlyList<ReviewUnitPart> Parts, string UserPrompt);

/// <summary>One source part that could not fit safely in any configured review conversation</summary>
public sealed record ReviewPartBuildFailure(ReviewUnitPart Part, string Reason);

/// <summary>Prepared clustered review work plus deterministic file and part dispositions that require no model call</summary>
public sealed record ClusteredReviewBuild(IReadOnlyList<ReviewExecutionUnit> Units, IReadOnlyList<ReviewUnitPart> Parts, IReadOnlyList<FileReviewResult> ImmediateResults,
    IReadOnlyList<ReviewPartBuildFailure> PartFailures);

/// <summary>Loads, chunks, and packs planned files into exact character-bounded sequential review units</summary>
public sealed class ClusteredReviewUnitBuilder
{
    private const int InitialContextPercent = 55;
    private const int MinimumSourceBudget = 256;
    private const int ChangedLineContext = 20;

    private readonly RepositoryReviewSourceLoader _sourceLoader;
    private readonly int _maxFileLines;
    private readonly int _maxContextCharacters;
    private readonly string _systemPrompt;
    private readonly string _repositorySummary;

    /// <summary>Creates a unit builder for one repository run and primary review prompt</summary>
    public ClusteredReviewUnitBuilder(RepositoryReviewSourceLoader sourceLoader, int maxFileLines, int maxContextCharacters, string systemPrompt, string repositorySummary)
    {
        ArgumentNullException.ThrowIfNull(sourceLoader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContextCharacters);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositorySummary);

        _sourceLoader = sourceLoader;
        _maxFileLines = maxFileLines;
        _maxContextCharacters = maxContextCharacters;
        _systemPrompt = systemPrompt;
        _repositorySummary = repositorySummary;
    }

    /// <summary>Prepares every planned candidate exactly once and splits oversized source across bounded execution units</summary>
    public async Task<ClusteredReviewBuild> BuildAsync(RepositoryReviewPlan plan, IReadOnlyList<ChangedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(files);

        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, ChangedFile> filesByPath = files.ToDictionary(file => file.Path, comparer);
        var units = new List<ReviewExecutionUnit>();
        var allParts = new List<ReviewUnitPart>();
        var immediateResults = new List<FileReviewResult>();
        var partFailures = new List<ReviewPartBuildFailure>();
        var plannedPaths = new HashSet<string>(comparer);
        var deferredPaths = new HashSet<string>(plan.Deferred.Select(item => item.Path), comparer);
        int partSequence = 0;

        foreach (RepositoryReviewUnit plannedUnit in plan.Units.OrderBy(unit => unit.Priority).ThenBy(unit => unit.Id, StringComparer.Ordinal))
        {
            IReadOnlyList<string> supportingPaths = BoundSupportingPaths(plannedUnit);
            string emptyPrompt = BuildPrompt(plannedUnit.Id, plannedUnit.Rationale, supportingPaths, []);
            int sourceBudget = InitialCharacterLimit - _systemPrompt.Length - emptyPrompt.Length;
            if (sourceBudget < MinimumSourceBudget)
            {
                throw new InformantFatalException(
                    $"maxContextCharacters {_maxContextCharacters} leaves fewer than {MinimumSourceBudget} characters for source after the review prompt and unit metadata; "
                    + "increase the context budget or shorten the prompt");
            }

            var plannedParts = new List<ReviewUnitPart>();
            foreach (string path in plannedUnit.Paths)
            {
                if (!plannedPaths.Add(path))
                {
                    throw new InvalidOperationException($"Validated review plan assigned path '{path}' more than once");
                }

                if (!filesByPath.TryGetValue(path, out ChangedFile? file))
                {
                    throw new InvalidOperationException($"Validated review plan path '{path}' is absent from the detected review set");
                }

                PreparedReviewFile prepared = await _sourceLoader.LoadAsync(file, cancellationToken);
                if (prepared.ImmediateResult is not null)
                {
                    immediateResults.Add(prepared.ImmediateResult);
                    continue;
                }

                string[] lines = prepared.Lines!;
                IReadOnlyList<SourceChunk> chunks = EnsureChunksFitInitialBudget(plannedUnit, supportingPaths, file, lines,
                    SelectChunks(file, lines, sourceBudget, plannedUnit.ChangedLinesOnly));
                for (int index = 0; index < chunks.Count; index++)
                {
                    SourceChunk chunk = chunks[index];
                    string partId = $"part-{++partSequence:D6}";
                    string sourceBlock = BuildSourceBlock(partId, file, lines, chunk, index + 1, chunks.Count, plannedUnit.ChangedLinesOnly);
                    int contentCharacters = CountCharacters(lines, chunk);
                    var part = new ReviewUnitPart(partId, file, index + 1, chunks.Count, chunk.StartLine, chunk.EndLine, sourceBlock, contentCharacters, plannedUnit.ChangedLinesOnly);
                    plannedParts.Add(part);
                    allParts.Add(part);
                }
            }

            PackPlannedUnit(plannedUnit, supportingPaths, plannedParts, units, partFailures);
        }

        foreach (ChangedFile file in files.Where(file => !plannedPaths.Contains(file.Path) && !deferredPaths.Contains(file.Path)))
        {
            PreparedReviewFile prepared = await _sourceLoader.LoadAsync(file, cancellationToken);
            immediateResults.Add(prepared.ImmediateResult ?? new FileReviewResult(file, FileReviewStatus.Failed, null, 0, 0,
                "validated review plan did not assign reviewable source", FailureKind: FileReviewFailureKind.Repository));
        }

        return new ClusteredReviewBuild(units, allParts, immediateResults, partFailures);
    }

    private int InitialCharacterLimit => _maxContextCharacters * InitialContextPercent / 100;

    private void PackPlannedUnit(RepositoryReviewUnit plannedUnit, IReadOnlyList<string> supportingPaths, IReadOnlyList<ReviewUnitPart> parts, List<ReviewExecutionUnit> units,
        List<ReviewPartBuildFailure> failures)
    {
        var current = new List<ReviewUnitPart>();
        int segment = 1;
        foreach (ReviewUnitPart part in parts)
        {
            string executionId = $"{plannedUnit.Id}-segment-{segment:D3}";
            ReviewUnitPart[] candidateParts = [.. current, part];
            string candidatePrompt = BuildPrompt(executionId, plannedUnit.Rationale, supportingPaths, candidateParts);
            if (_systemPrompt.Length + candidatePrompt.Length <= InitialCharacterLimit)
            {
                current.Add(part);
                continue;
            }

            if (current.Count > 0)
            {
                AddUnit(units, executionId, plannedUnit.Rationale, supportingPaths, current);
                current.Clear();
                segment++;
                executionId = $"{plannedUnit.Id}-segment-{segment:D3}";
                candidatePrompt = BuildPrompt(executionId, plannedUnit.Rationale, supportingPaths, [part]);
            }

            if (_systemPrompt.Length + candidatePrompt.Length <= InitialCharacterLimit)
            {
                current.Add(part);
            }
            else
            {
                failures.Add(new ReviewPartBuildFailure(part,
                    $"source part requires {_systemPrompt.Length + candidatePrompt.Length} initial characters but the 55 percent initial-context limit is {InitialCharacterLimit}"));
            }
        }

        if (current.Count > 0)
        {
            string executionId = $"{plannedUnit.Id}-segment-{segment:D3}";
            AddUnit(units, executionId, plannedUnit.Rationale, supportingPaths, current);
        }
    }

    private void AddUnit(List<ReviewExecutionUnit> units, string executionId, string rationale, IReadOnlyList<string> supportingPaths, IReadOnlyList<ReviewUnitPart> parts)
    {
        string prompt = BuildPrompt(executionId, rationale, supportingPaths, parts);
        units.Add(new ReviewExecutionUnit(executionId, rationale, supportingPaths, [.. parts], prompt));
    }

    private IReadOnlyList<string> BoundSupportingPaths(RepositoryReviewUnit plannedUnit)
    {
        var included = plannedUnit.SupportingPaths.ToList();
        while (included.Count > 0 && _systemPrompt.Length + BuildPrompt(plannedUnit.Id, plannedUnit.Rationale, included, []).Length >= InitialCharacterLimit - MinimumSourceBudget)
        {
            included.RemoveAt(included.Count - 1);
        }

        return included;
    }

    private string BuildPrompt(string unitId, string rationale, IReadOnlyList<string> supportingPaths, IReadOnlyList<ReviewUnitPart> parts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Review this bounded cluster of related repository source.");
        builder.AppendLine($"Review unit: {unitId}");
        builder.AppendLine($"Repository summary: {_repositorySummary}");
        builder.AppendLine($"Unit rationale: {rationale}");
        if (supportingPaths.Count > 0)
        {
            builder.AppendLine($"Suggested supporting paths available through read_file_lines: {string.Join(", ", supportingPaths)}");
        }

        builder.AppendLine();
        builder.AppendLine("Return prose for every supplied source part in the same order. Start each part with its exact result marker, for example:");
        builder.AppendLine("=== BUGSWATTER RESULT part-000001 ===");
        builder.AppendLine("Do not omit a marker, even when that part has no findings. After all marked prose, append the required fenced structured-findings JSON block from the system prompt.");

        foreach (ReviewUnitPart part in parts)
        {
            builder.AppendLine();
            builder.Append(part.SourceBlock);
        }

        return builder.ToString();
    }

    private IReadOnlyList<SourceChunk> SelectChunks(ChangedFile file, string[] lines, int sourceBudget, bool changedLinesOnly)
    {
        if (!changedLinesOnly || file.ChangedRanges.Count == 0)
        {
            return SourceChunker.Split(lines, _maxFileLines, sourceBudget);
        }

        var windows = new List<LineRange>();
        foreach (LineRange range in file.ChangedRanges.OrderBy(range => range.Start))
        {
            int start = Math.Max(1, range.Start - ChangedLineContext);
            int end = Math.Min(lines.Length, range.End + ChangedLineContext);
            if (start > end)
            {
                continue;
            }

            if (windows.Count > 0 && start <= windows[^1].End + 1)
            {
                windows[^1] = windows[^1] with { End = Math.Max(windows[^1].End, end) };
            }
            else
            {
                windows.Add(new LineRange(start, end));
            }
        }

        if (windows.Count == 0)
        {
            return SourceChunker.Split(lines, _maxFileLines, sourceBudget);
        }

        var chunks = new List<SourceChunk>();
        foreach (LineRange window in windows)
        {
            string[] windowLines = lines[(window.Start - 1)..window.End];
            foreach (SourceChunk localChunk in SourceChunker.Split(windowLines, _maxFileLines, sourceBudget))
            {
                chunks.Add(new SourceChunk(window.Start + localChunk.StartLine - 1, window.Start + localChunk.EndLine - 1, localChunk.HardCut));
            }
        }

        return chunks;
    }

    private IReadOnlyList<SourceChunk> EnsureChunksFitInitialBudget(RepositoryReviewUnit plannedUnit, IReadOnlyList<string> supportingPaths, ChangedFile file, string[] lines,
        IReadOnlyList<SourceChunk> chunks)
    {
        var pending = new Stack<SourceChunk>(chunks.Reverse());
        var fitted = new List<SourceChunk>();

        while (pending.Count > 0)
        {
            SourceChunk chunk = pending.Pop();
            if (FitsInitialBudget(plannedUnit, supportingPaths, file, lines, chunk))
            {
                fitted.Add(chunk);
                continue;
            }

            if (chunk.StartLine == chunk.EndLine)
            {
                // Keep an intrinsically unfit line so PackPlannedUnit records the deterministic part failure
                fitted.Add(chunk);
                continue;
            }

            int middle = chunk.StartLine + (chunk.EndLine - chunk.StartLine) / 2;
            pending.Push(new SourceChunk(middle + 1, chunk.EndLine, chunk.HardCut));
            pending.Push(new SourceChunk(chunk.StartLine, middle, true));
        }

        return fitted;
    }

    private bool FitsInitialBudget(RepositoryReviewUnit plannedUnit, IReadOnlyList<string> supportingPaths, ChangedFile file, string[] lines, SourceChunk chunk)
    {
        const int ProbePartNumber = int.MaxValue;
        const string ProbePartId = "part-2147483647";
        string sourceBlock = BuildSourceBlock(ProbePartId, file, lines, chunk, ProbePartNumber, ProbePartNumber, plannedUnit.ChangedLinesOnly);
        var part = new ReviewUnitPart(ProbePartId, file, ProbePartNumber, ProbePartNumber, chunk.StartLine, chunk.EndLine, sourceBlock, 0, plannedUnit.ChangedLinesOnly);
        string prompt = BuildPrompt($"{plannedUnit.Id}-segment-{ProbePartNumber}", plannedUnit.Rationale, supportingPaths, [part]);
        return _systemPrompt.Length + prompt.Length <= InitialCharacterLimit;
    }

    private static string BuildSourceBlock(string partId, ChangedFile file, string[] lines, SourceChunk chunk, int partNumber, int totalParts, bool changedLinesOnly)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"=== BUGSWATTER SOURCE {partId} ===");
        builder.AppendLine($"File: {file.Path}");
        builder.AppendLine($"Change status: {file.Kind}");
        builder.AppendLine($"File part: {partNumber} of {totalParts}; lines {chunk.StartLine}-{chunk.EndLine}");
        builder.AppendLine($"Changed line ranges: {(file.ChangedRanges.Count == 0 ? "(entire file or deletion)" : string.Join(", ", file.ChangedRanges))}");
        if (changedLinesOnly)
        {
            builder.AppendLine($"Coverage: mandatory changed lines with up to {ChangedLineContext} surrounding context lines; the full-file deep review was adaptively deferred.");
        }

        if (file.Kind == ChangeKind.Deleted)
        {
            builder.AppendLine("This is immutable baseline content removed by the change. Review the effects of its deletion on surviving repository content.");
        }

        builder.AppendLine("Numbered source:");
        for (int lineNumber = chunk.StartLine; lineNumber <= chunk.EndLine; lineNumber++)
        {
            builder.AppendLine($"{lineNumber,6} | {lines[lineNumber - 1]}");
        }

        return builder.ToString();
    }

    private static int CountCharacters(string[] lines, SourceChunk chunk)
    {
        int characters = 0;
        for (int index = chunk.StartLine - 1; index < chunk.EndLine; index++)
        {
            characters += lines[index].Length;
        }

        return characters;
    }
}
