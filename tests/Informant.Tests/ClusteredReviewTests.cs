using System.Net;

namespace Informant.Tests;

/// <summary>Tests bounded clustered source preparation, response attribution, and file aggregation</summary>
public sealed class ClusteredReviewTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    /// <inheritdoc />
    public void Dispose() => _directory.Dispose();

    /// <summary>Verifies related small files share one bounded execution unit</summary>
    [Fact]
    public async Task RelatedSmallFilesArePackedIntoOneBoundedUnit()
    {
        Write("src/One.cs", "first\nsecond");
        Write("src/Two.cs", "third\nfourth");
        ChangedFile[] files = [File("src/One.cs"), File("src/Two.cs")];
        RepositoryReviewPlan plan = Plan(new RepositoryReviewUnit("core", 1, "related implementation", files.Select(file => file.Path).ToArray(), ["README.md"]));
        var builder = new ClusteredReviewUnitBuilder(new RepositoryReviewSourceLoader(_directory.Path), 100, 12000, "system prompt", "repository summary");

        ClusteredReviewBuild build = await builder.BuildAsync(plan, files);

        ReviewExecutionUnit unit = Assert.Single(build.Units);
        Assert.Equal(2, unit.Parts.Count);
        Assert.Equal(["src/One.cs", "src/Two.cs"], unit.Parts.Select(part => part.File.Path));
        Assert.True("system prompt".Length + unit.UserPrompt.Length <= 12000 * 55 / 100);
        Assert.Empty(build.ImmediateResults);
        Assert.Empty(build.PartFailures);
    }

    /// <summary>Verifies source chunking preserves every line and every packed prompt stays below its exact initial budget</summary>
    [Fact]
    public async Task LargeFilePartsCoverEveryLineWithoutExceedingBudget()
    {
        string[] lines = [.. Enumerable.Range(1, 25).Select(number => $"line {number} {new string('x', 40)}")];
        Write("src/Large.cs", string.Join('\n', lines));
        ChangedFile file = File("src/Large.cs");
        var builder = new ClusteredReviewUnitBuilder(new RepositoryReviewSourceLoader(_directory.Path), 4, 8000, "system prompt", "repository summary");

        ClusteredReviewBuild build = await builder.BuildAsync(Plan(new RepositoryReviewUnit("large", 1, "large file", [file.Path], [])), [file]);

        ReviewUnitPart[] parts = [.. build.Parts.OrderBy(part => part.StartLine)];
        Assert.True(parts.Length > 1);
        Assert.Equal(1, parts[0].StartLine);
        Assert.Equal(lines.Length, parts[^1].EndLine);
        Assert.All(parts.Zip(parts.Skip(1)), pair => Assert.Equal(pair.First.EndLine + 1, pair.Second.StartLine));
        Assert.All(build.Units, unit => Assert.True("system prompt".Length + unit.UserPrompt.Length <= 8000 * 55 / 100));
        Assert.Empty(build.PartFailures);
    }

    /// <summary>Verifies rendered source labels and numbered-line overhead are included before a source part is scheduled</summary>
    [Fact]
    public async Task RenderedSourcePartIsSplitBeforeItExceedsTheInitialPromptBudget()
    {
        string[] lines = [new string('x', 1600), new string('y', 1600), new string('z', 1600)];
        Write("src/RenderedBudget.cs", string.Join('\n', lines));
        ChangedFile file = File("src/RenderedBudget.cs");
        var builder = new ClusteredReviewUnitBuilder(new RepositoryReviewSourceLoader(_directory.Path), 100, 10000, "system prompt", "repository summary");

        ClusteredReviewBuild build = await builder.BuildAsync(Plan(new RepositoryReviewUnit("rendered", 1, "rendered source budget", [file.Path], [])), [file]);

        ReviewUnitPart[] parts = [.. build.Parts.OrderBy(part => part.StartLine)];
        Assert.Equal(2, parts.Length);
        Assert.Equal(1, parts[0].StartLine);
        Assert.Equal(lines.Length, parts[^1].EndLine);
        Assert.All(build.Units, unit => Assert.True("system prompt".Length + unit.UserPrompt.Length <= 10000 * 55 / 100));
        Assert.Empty(build.PartFailures);
    }

    /// <summary>Verifies cancellation reaches Git when loading deleted baseline content</summary>
    [Fact]
    public async Task DeletedSourceLoadingPropagatesCancellationToGit()
    {
        var loader = new RepositoryReviewSourceLoader(_directory.Path, git: new GitRunner(TestGit.ExecutablePath));
        var file = new ChangedFile("deleted.cs", ChangeKind.Deleted, [], new string('a', 40));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(file, cancellation.Token));
    }

    /// <summary>Verifies deterministic exclusions never enter a model execution unit</summary>
    [Fact]
    public async Task OversizedFileBecomesImmediateNotReviewableResult()
    {
        Write("large.txt", "more than five bytes");
        ChangedFile file = File("large.txt");
        var builder = new ClusteredReviewUnitBuilder(new RepositoryReviewSourceLoader(_directory.Path, 5), 100, 8000, "system prompt", "repository summary");

        ClusteredReviewBuild build = await builder.BuildAsync(Plan(new RepositoryReviewUnit("large", 1, "oversized", [file.Path], [])), [file]);

        FileReviewResult result = Assert.Single(build.ImmediateResults);
        Assert.Equal(FileReviewStatus.NotReviewable, result.Status);
        Assert.Contains("maxFileBytes", result.SkipReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(build.Units);
    }

    /// <summary>Verifies exact model markers attribute prose and structured severities to each supplied source part</summary>
    [Fact]
    public void ParserAttributesMarkedFindingsToExactParts()
    {
        ReviewExecutionUnit unit = Unit(Part("part-000001", "src/One.cs", 1, 1), Part("part-000002", "src/Two.cs", 1, 1));
        const string Response = """
            === BUGSWATTER RESULT part-000001 ===
            One finding
            === BUGSWATTER RESULT part-000002 ===
            Two finding
            ```json
            { "findings": [
              { "file": "src/One.cs", "line": 2, "severity": "high", "summary": "one" },
              { "file": "src/Two.cs", "line": 4, "severity": "low", "summary": "two" }
            ] }
            ```
            """;

        bool parsed = ClusteredReviewResponseParser.TryParse(Response, unit, out IReadOnlyList<ReviewUnitPartResult> results);

        Assert.True(parsed);
        Assert.Equal("One finding", results[0].Findings);
        Assert.Equal(Severity.High, results[0].CandidateSeverity);
        Assert.True(results[0].CandidateSeverityDetermined);
        Assert.Equal("Two finding", results[1].Findings);
        Assert.Equal(Severity.Low, results[1].CandidateSeverity);
    }

    /// <summary>Verifies attributable prose remains usable while malformed severity JSON fails safe as undetermined</summary>
    [Fact]
    public void ParserKeepsMarkedProseButMarksMalformedSeverityUndetermined()
    {
        ReviewExecutionUnit unit = Unit(Part("part-000001", "src/One.cs", 1, 1));
        const string Response = """
            === BUGSWATTER RESULT part-000001 ===
            Review prose
            ```json
            { nope }
            ```
            """;

        Assert.True(ClusteredReviewResponseParser.TryParse(Response, unit, out IReadOnlyList<ReviewUnitPartResult> results));
        ReviewUnitPartResult result = Assert.Single(results);
        Assert.Contains("Review prose", result.Findings);
        Assert.False(result.CandidateSeverityDetermined);
        Assert.Equal(Severity.None, result.CandidateSeverity);
    }

    /// <summary>Verifies invented structured paths make severity undetermined without losing marked prose</summary>
    [Fact]
    public void ParserRejectsInventedStructuredFindingPath()
    {
        ReviewExecutionUnit unit = Unit(Part("part-000001", "src/One.cs", 1, 1));
        const string Response = """
            === BUGSWATTER RESULT part-000001 ===
            Review prose
            ```json
            { "findings": [ { "file": "outside.cs", "line": 1, "severity": "critical", "summary": "invented" } ] }
            ```
            """;

        Assert.True(ClusteredReviewResponseParser.TryParse(Response, unit, out IReadOnlyList<ReviewUnitPartResult> results));
        Assert.False(Assert.Single(results).CandidateSeverityDetermined);
    }

    /// <summary>Verifies one failed part produces a partial file result and prevents determined severity</summary>
    [Fact]
    public void AggregatorPreservesCompletedPartsAndReportsFailedRemainder()
    {
        ChangedFile file = File("src/Large.cs");
        ReviewUnitPart first = Part("part-000001", file.Path, 1, 2);
        var second = new ReviewUnitPart("part-000002", file, 2, 2, 3, 3, "source", 6);
        ReviewExecutionUnit firstUnit = Unit(first);
        ReviewExecutionUnit secondUnit = Unit(second);
        var build = new ClusteredReviewBuild([firstUnit, secondUnit], [first, second], [], []);
        var firstResult = new ReviewUnitResult(firstUnit, [new ReviewUnitPartResult(first, "first findings", Severity.Medium, true)], FileReviewFailureKind.None, null, "model-a", "primary");
        var secondResult = new ReviewUnitResult(secondUnit, [], FileReviewFailureKind.Model, "endpoint failed", "model-a", "primary");

        FileReviewResult aggregate = Assert.Single(ClusteredReviewResultAggregator.Build([file], build, [firstResult, secondResult]));

        Assert.Equal(FileReviewStatus.Partial, aggregate.Status);
        Assert.Equal(1, aggregate.CompletedChunks);
        Assert.Equal(2, aggregate.TotalChunks);
        Assert.Contains("first findings", aggregate.Findings);
        Assert.Contains("endpoint failed", aggregate.SkipReason);
        Assert.False(aggregate.CandidateSeverityDetermined);
    }

    /// <summary>Verifies a clustered model conversation can request bounded supporting lines and still attribute its final response</summary>
    [Fact]
    public async Task ReviewerHandlesScriptedToolCallAndFinalResponse()
    {
        Write("support.txt", "alpha\nbravo");
        using var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call-1", ReadFileLinesTool.ToolName,
            StubHttpMessageHandler.ReadArguments("support.txt", 1, 2))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("""
            === BUGSWATTER RESULT part-000001 ===
            Supporting content confirmed the behavior
            ```json
            { "findings": [] }
            ```
            """));
        using var http = new HttpClient(handler);
        var client = new ModelClient(http, "http://model.test/v1", "test-model", TimeSpan.FromSeconds(5));
        var loop = new ToolCallLoop(client, new ReadFileLinesTool(_directory.Path), 12000);
        var reviewer = new ClusteredReviewReviewer(loop, "system prompt", 0);

        ReviewUnitResult result = await reviewer.ReviewAsync(Unit(Part("part-000001", "src/One.cs", 1, 1)));

        Assert.True(result.Succeeded);
        Assert.Contains("Supporting content confirmed", Assert.Single(result.PartResults).Findings);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("alpha", handler.RequestBodies[1]);
    }

    /// <summary>Verifies exhaustive clustered aggregation accounts for every candidate, including deterministic exclusions</summary>
    [Fact]
    public async Task ExhaustiveAggregationReturnsOneDispositionPerCandidate()
    {
        Write("src/One.cs", "source");
        Write("empty.txt", "");
        ChangedFile[] files = [File("src/One.cs"), File("empty.txt")];
        RepositoryReviewPlan plan = Plan(new RepositoryReviewUnit("all", 1, "complete coverage", files.Select(file => file.Path).ToArray(), []));
        var builder = new ClusteredReviewUnitBuilder(new RepositoryReviewSourceLoader(_directory.Path), 100, 8000, "system prompt", "repository summary");
        ClusteredReviewBuild build = await builder.BuildAsync(plan, files);
        ReviewUnitResult[] unitResults =
        [
            .. build.Units.Select(unit => new ReviewUnitResult(unit,
                unit.Parts.Select(part => new ReviewUnitPartResult(part, "reviewed", Severity.None, true)).ToArray(), FileReviewFailureKind.None, null))
        ];

        IReadOnlyList<FileReviewResult> results = ClusteredReviewResultAggregator.Build(files, build, unitResults);

        Assert.Equal(files.Select(file => file.Path), results.Select(result => result.File.Path));
        Assert.Equal(FileReviewStatus.Reviewed, results[0].Status);
        Assert.Equal(FileReviewStatus.NotReviewable, results[1].Status);
        Assert.True(ReviewCompletion.CanAdvanceBaseline(results));
    }

    /// <summary>Verifies mandatory adaptive coverage includes changed lines with bounded surrounding context instead of the entire deferred file</summary>
    [Fact]
    public async Task MandatoryChangedCoverageUsesFocusedLineWindows()
    {
        string[] lines = [.. Enumerable.Range(1, 100).Select(number => $"line {number}")];
        Write("src/Focused.cs", string.Join('\n', lines));
        var file = new ChangedFile("src/Focused.cs", ChangeKind.Modified, [new LineRange(50, 50)]);
        var unit = new RepositoryReviewUnit("mandatory", 1, "changed content", [file.Path], [], ChangedLinesOnly: true);
        var builder = new ClusteredReviewUnitBuilder(new RepositoryReviewSourceLoader(_directory.Path), 200, 12000, "system prompt", "repository summary");

        ClusteredReviewBuild build = await builder.BuildAsync(Plan(unit), [file]);

        ReviewUnitPart part = Assert.Single(build.Parts);
        Assert.True(part.MandatoryChangedContent);
        Assert.Equal(30, part.StartLine);
        Assert.Equal(70, part.EndLine);
        Assert.DoesNotContain("     1 | line 1", part.SourceBlock);
        Assert.Contains("    50 | line 50", part.SourceBlock);
    }

    /// <summary>Verifies completed mandatory changed-content review remains labeled deferred rather than overstating full-file review</summary>
    [Fact]
    public void AggregatorLabelsMandatoryChangedCoverageAsDeferred()
    {
        var file = new ChangedFile("src/Focused.cs", ChangeKind.Modified, [new LineRange(50, 50)]);
        ReviewUnitPart part = new("part-000001", file, 1, 1, 30, 70, "source", 100, MandatoryChangedContent: true);
        ReviewExecutionUnit unit = Unit(part);
        var build = new ClusteredReviewBuild([unit], [part], [], []);
        var unitResult = new ReviewUnitResult(unit, [new ReviewUnitPartResult(part, "changed lines look safe", Severity.None, true)], FileReviewFailureKind.None, null);
        RepositoryReviewDeferral[] deferrals = [new(file.Path, "lower priority")];

        FileReviewResult result = Assert.Single(ClusteredReviewResultAggregator.Build([file], build, [unitResult], deferrals));

        Assert.Equal(FileReviewStatus.Deferred, result.Status);
        Assert.True(result.CandidateSeverityDetermined);
        Assert.Contains("mandatory changed content reviewed", result.SkipReason);
        Assert.False(result.FullyReviewed);
    }

    private static RepositoryReviewPlan Plan(params RepositoryReviewUnit[] units) => new("repository", units, [], [], false, false, []);

    private static ChangedFile File(string path) => new(path, ChangeKind.FullReview, []);

    private static ReviewUnitPart Part(string id, string path, int partNumber, int totalParts) =>
        new(id, File(path), partNumber, totalParts, partNumber, partNumber, "source", 6);

    private static ReviewExecutionUnit Unit(params ReviewUnitPart[] parts) => new("unit", "rationale", [], parts, "prompt");

    private void Write(string relativePath, string contents)
    {
        string path = Path.Combine(_directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
    }
}
