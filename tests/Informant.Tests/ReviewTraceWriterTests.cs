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
        Assert.Equal(new ReviewTraceSummary("Informant-Trace-2026-07-14_12-00-00.jsonl", 4, 1, 0, 1, 0, 1), summary);
        string jsonl = string.Join('\n', lines);
        Assert.DoesNotContain("sourceBody", jsonl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", jsonl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", jsonl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", jsonl, StringComparison.OrdinalIgnoreCase);
    }
}
