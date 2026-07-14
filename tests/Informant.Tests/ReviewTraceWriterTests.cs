using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Informant.Tests;

/// <summary>Tests metadata-only JSONL trace persistence and aggregate counts</summary>
public sealed class ReviewTraceWriterTests : IDisposable
{
    private readonly TempDirectory _reports = new();

    /// <inheritdoc />
    public void Dispose() => _reports.Dispose();

    /// <summary>Verifies trace records are ordered, flushed, and contain metadata without prompt, source, response, or secret bodies</summary>
    [Fact]
    public void WritesOrderedMetadataOnlyEventsAndSummary()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));
        var entry = new RepositoryManifestEntry("source.cs", "100644", "blob", "object", 10, 1, "hash", ".cs", true, RepositoryManifestDisposition.Text, ChangeKind.Modified);
        var manifest = new RepositoryManifest("repository", "main", "tree", "baseline", "tip", ReviewMode.Changed, "2026-07-14_12-00-00", timeProvider.GetUtcNow(), [entry]);
        var context = new ReviewTraceContext { UnitId = "unit-1" };
        string tracePath;
        ReviewTraceSummary summary;

        using (var writer = new ReviewTraceWriter(_reports.Path, manifest.RunStamp, timeProvider))
        {
            writer.WriteManifestCreated(manifest);
            writer.CreateContextSelectionObserver("model", "primary", context)(new ReviewContextSelectionEvent("source.cs", 1, 10, 10, 200));
            writer.CreateReadObserver("model", "primary", context)(new RepositoryReadAuditEvent("source.cs", "source.cs", 1, 20, RepositoryReadOutcome.PartiallyServed,
                1, 5, 5, 100, 300, 20, "CharacterLimit", 6, 12));
            writer.CreateToolObserver("model", "primary", context)(new ModelToolCallAuditEvent("read_file_lines", ModelToolCallOutcome.Executed, 75, 300, 13));
            tracePath = writer.TracePath;
            summary = writer.Summary;
        }

        string[] lines = File.ReadAllLines(tracePath);
        Assert.Equal(4, lines.Length);
        using JsonDocument manifestEvent = JsonDocument.Parse(lines[0]);
        using JsonDocument contextEvent = JsonDocument.Parse(lines[1]);
        using JsonDocument readEvent = JsonDocument.Parse(lines[2]);
        using JsonDocument toolEvent = JsonDocument.Parse(lines[3]);
        Assert.Equal(1, manifestEvent.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("context_selected", contextEvent.RootElement.GetProperty("eventType").GetString());
        Assert.Equal(200, contextEvent.RootElement.GetProperty("returnedContentCharacters").GetInt32());
        Assert.Equal("repository_read", readEvent.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("unit-1", readEvent.RootElement.GetProperty("unitId").GetString());
        Assert.Equal(4, toolEvent.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(new ReviewTraceSummary("Informant-Trace-2026-07-14_12-00-00.jsonl", 4, 1, 0, 1, 0, 1, 0), summary);
        string jsonl = string.Join('\n', lines);
        Assert.DoesNotContain("sourceBody", jsonl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", jsonl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", jsonl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", jsonl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies planning, unit, and coverage events contain counts and outcomes without prompts, source, findings, or deferral reasons</summary>
    [Fact]
    public void WritesMetadataOnlyPlanningUnitAndCoverageEvents()
    {
        var target = new PrimaryModelTarget("primary", "http://model.test/v1", "model", false);
        var file = new ChangedFile("src/One.cs", ChangeKind.Modified, [new LineRange(1, 1)]);
        var part = new ReviewUnitPart("part-000001", file, 1, 1, 1, 1, "SECRET SOURCE", 13);
        var unit = new ReviewExecutionUnit("unit-1", "SECRET RATIONALE", [], [part], "SECRET PROMPT");
        var plan = new RepositoryReviewPlan("summary", [new RepositoryReviewUnit("unit-1", 1, "rationale", [file.Path], [])], [], [], false, false, []);
        var planning = new RepositoryPlanningResult(plan, 1, 1);
        var unitResult = new ReviewUnitResult(unit, [new ReviewUnitPartResult(part, "SECRET FINDING", Severity.None, true)], FileReviewFailureKind.None, null, "model", "primary");
        var coverage = new ReviewCoverageLedger(ReviewStrategy.Exhaustive,
            [new ReviewCoverageEntry(file.Path, file.Kind, true, false, false, ReviewCoverageOutcome.DeepReviewed, "SECRET REASON")]);
        string path;
        ReviewTraceSummary summary;

        using (var writer = new ReviewTraceWriter(_reports.Path, "2026-07-14_13-00-00"))
        {
            writer.WritePlanningCompleted(planning, plan, target);
            writer.WritePlanningContextSelected(2, new RepositoryContextItem("README.md", 10, "SECRET INITIAL SOURCE", LineCount: 4, ContentCharacters: 100), target);
            Action<ModelCallProgress> modelObserver = writer.CreateModelCallObserver("primary", new ReviewTraceContext { UnitId = "unit-1" });
            DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
            modelObserver(new ModelCallProgress(ModelCallState.Started, "model", startedUtc, TimeSpan.Zero, null, 500));
            modelObserver(new ModelCallProgress(ModelCallState.Completed, "model", startedUtc, TimeSpan.FromSeconds(1), new ModelTokenUsage(20, 5, 25), 500));
            writer.WriteReviewUnitStarted(unit, target);
            writer.WriteReviewUnitCompleted(unitResult, TimeSpan.FromSeconds(2));
            writer.WriteCoverageCreated(coverage);
            path = writer.TracePath;
            summary = writer.Summary;
        }

        string jsonl = File.ReadAllText(path);
        Assert.Contains("planning_completed", jsonl);
        Assert.Contains("planning_context_selected", jsonl);
        Assert.Contains("model_request_completed", jsonl);
        Assert.Contains("\"requestCharacters\":500", jsonl);
        Assert.Contains("\"totalTokens\":25", jsonl);
        Assert.Contains("README.md", jsonl);
        Assert.Contains("\"returnedContentCharacters\":100", jsonl);
        Assert.Contains("review_unit_started", jsonl);
        Assert.Contains("review_unit_completed", jsonl);
        Assert.Contains("coverage_created", jsonl);
        Assert.DoesNotContain("SECRET", jsonl);
        Assert.Equal(1, summary.ModelRequestCount);
    }
}
