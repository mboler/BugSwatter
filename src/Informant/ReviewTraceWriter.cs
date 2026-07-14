using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugSwatter.AI;

namespace Informant;

/// <summary>Mutable sequential review context attached to trace events</summary>
public sealed class ReviewTraceContext
{
    /// <summary>Current file or clustered review-unit identifier</summary>
    public string? UnitId { get; set; }
}

/// <summary>Aggregate counts for one metadata-only review trace</summary>
public sealed record ReviewTraceSummary(string FileName, long EventCount, int ReadRequestCount, int ServedReadCount, int PartiallyServedReadCount, int RejectedReadCount,
    int ToolCallEventCount);

/// <summary>Versioned metadata-only JSONL record describing one controller or model action</summary>
public sealed record ReviewTraceRecord
{
    /// <summary>Trace record schema version</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Monotonic sequence within this trace</summary>
    public long Sequence { get; init; }

    /// <summary>UTC time the event was written</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Stable event category</summary>
    public string EventType { get; init; } = "";

    /// <summary>Configured model name when the event belongs to a model call</summary>
    public string? ModelName { get; init; }

    /// <summary>Configured model profile name</summary>
    public string? ProfileName { get; init; }

    /// <summary>Current review file or unit</summary>
    public string? UnitId { get; init; }

    /// <summary>Requested model tool name</summary>
    public string? ToolName { get; init; }

    /// <summary>Raw model-requested path</summary>
    public string? RequestedPath { get; init; }

    /// <summary>Original requested-path length before any audit-record truncation</summary>
    public int? RequestedPathCharacters { get; init; }

    /// <summary>Whether the recorded requested path is an explicitly marked bounded prefix</summary>
    public bool? RequestedPathTruncated { get; init; }

    /// <summary>Manifest-normalized path when validation reached it</summary>
    public string? NormalizedPath { get; init; }

    /// <summary>Requested first line</summary>
    public int? RequestedStartLine { get; init; }

    /// <summary>Requested last line</summary>
    public int? RequestedEndLine { get; init; }

    /// <summary>First line actually returned</summary>
    public int? ReturnedStartLine { get; init; }

    /// <summary>Last line actually returned</summary>
    public int? ReturnedEndLine { get; init; }

    /// <summary>Number of source lines returned</summary>
    public int? ReturnedLineCount { get; init; }

    /// <summary>Number of source characters returned</summary>
    public int? ReturnedContentCharacters { get; init; }

    /// <summary>Serialized tool response length</summary>
    public int? ResponseCharacters { get; init; }

    /// <summary>Total lines in the safely read file</summary>
    public int? TotalLines { get; init; }

    /// <summary>Outcome name</summary>
    public string? Outcome { get; init; }

    /// <summary>Stable rejection or truncation reason</summary>
    public string? ReasonCode { get; init; }

    /// <summary>First line to request next when the response was partial</summary>
    public int? NextStartLine { get; init; }

    /// <summary>Tool argument JSON length without storing its content</summary>
    public int? ArgumentsCharacters { get; init; }

    /// <summary>Operation duration in milliseconds</summary>
    public long? DurationMilliseconds { get; init; }

    /// <summary>Manifest entry count for a manifest event</summary>
    public int? ManifestEntryCount { get; init; }

    /// <summary>Reviewable manifest entry count for a manifest event</summary>
    public int? ManifestReviewableCount { get; init; }

    /// <summary>Excluded manifest entry count for a manifest event</summary>
    public int? ManifestExcludedCount { get; init; }

    /// <summary>Selected manifest entry count for a manifest event</summary>
    public int? ManifestSelectedCount { get; init; }

    /// <summary>Number of planning batches represented by an event</summary>
    public int? PlanningBatchCount { get; init; }

    /// <summary>Number of planning batches sent to a model</summary>
    public int? ModelPlanningBatchCount { get; init; }

    /// <summary>Number of review units represented by an event</summary>
    public int? ReviewUnitCount { get; init; }

    /// <summary>Number of source parts represented by an event</summary>
    public int? ReviewUnitPartCount { get; init; }

    /// <summary>Number of candidate paths represented by an event</summary>
    public int? PathCount { get; init; }
}

/// <summary>Appends metadata-only review events to a retention-managed JSONL artifact</summary>
public sealed class ReviewTraceWriter : IDisposable
{
    private const int MaxRecordedPathCharacters = 4096;
    private const int MaxRecordedToolNameCharacters = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private readonly TimeProvider _timeProvider;
    private long _sequence;
    private int _readRequestCount;
    private int _servedReadCount;
    private int _partiallyServedReadCount;
    private int _rejectedReadCount;
    private int _toolCallEventCount;

    /// <summary>Creates a new trace artifact for one review run</summary>
    public ReviewTraceWriter(string reportDirectory, string runStamp, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runStamp);

        Directory.CreateDirectory(reportDirectory);
        TraceFileName = $"Informant-Trace-{runStamp}.jsonl";
        TracePath = Path.Combine(reportDirectory, TraceFileName);
        _writer = new StreamWriter(new FileStream(TracePath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Trace artifact filename without its directory</summary>
    public string TraceFileName { get; }

    /// <summary>Full trace artifact path</summary>
    public string TracePath { get; }

    /// <summary>Current aggregate trace counts</summary>
    public ReviewTraceSummary Summary
    {
        get
        {
            lock (_sync)
            {
                return new ReviewTraceSummary(TraceFileName, _sequence, _readRequestCount, _servedReadCount, _partiallyServedReadCount, _rejectedReadCount, _toolCallEventCount);
            }
        }
    }

    /// <summary>Records the rebuilt manifest summary without storing repository content</summary>
    public void WriteManifestCreated(RepositoryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Write(new ReviewTraceRecord
        {
            EventType = "manifest_created",
            ManifestEntryCount = manifest.EntryCount,
            ManifestReviewableCount = manifest.ReviewableCount,
            ManifestExcludedCount = manifest.ExcludedCount,
            ManifestSelectedCount = manifest.SelectedCount
        });
    }

    /// <summary>Records bounded planning counts and controller fallback state without storing prompts or model output</summary>
    public void WritePlanningCompleted(RepositoryPlanningResult planning, RepositoryReviewPlan finalPlan, PrimaryModelTarget target)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(finalPlan);
        ArgumentNullException.ThrowIfNull(target);
        Write(new ReviewTraceRecord
        {
            EventType = "planning_completed",
            ModelName = target.ModelName,
            ProfileName = target.Name,
            PlanningBatchCount = planning.BatchCount,
            ModelPlanningBatchCount = planning.ModelBatchCount,
            ReviewUnitCount = finalPlan.Units.Count,
            PathCount = finalPlan.Units.SelectMany(unit => unit.Paths).Distinct(StringComparer.Ordinal).Count(),
            Outcome = finalPlan.UsedFallback ? "Fallback" : finalPlan.CoverageRepaired ? "CoverageRepaired" : "Planned"
        });
    }

    /// <summary>Records the start of one clustered review unit without storing source or prompt text</summary>
    public void WriteReviewUnitStarted(ReviewExecutionUnit unit, PrimaryModelTarget target)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(target);
        Write(new ReviewTraceRecord
        {
            EventType = "review_unit_started",
            ModelName = target.ModelName,
            ProfileName = target.Name,
            UnitId = unit.Id,
            ReviewUnitPartCount = unit.Parts.Count,
            PathCount = unit.Parts.Select(part => part.File.Path).Distinct(StringComparer.Ordinal).Count()
        });
    }

    /// <summary>Records one clustered review-unit outcome without storing model findings</summary>
    public void WriteReviewUnitCompleted(ReviewUnitResult result, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(result);
        Write(new ReviewTraceRecord
        {
            EventType = "review_unit_completed",
            ModelName = result.ReviewModelName,
            ProfileName = result.ReviewModelProfile,
            UnitId = result.Unit.Id,
            ReviewUnitPartCount = result.Unit.Parts.Count,
            PathCount = result.Unit.Parts.Select(part => part.File.Path).Distinct(StringComparer.Ordinal).Count(),
            Outcome = result.Succeeded ? "Reviewed" : "Failed",
            ReasonCode = result.Succeeded ? null : result.FailureKind.ToString(),
            DurationMilliseconds = (long)duration.TotalMilliseconds
        });
    }

    /// <summary>Records aggregate coverage counts without storing source, findings, or path-specific reasons</summary>
    public void WriteCoverageCreated(ReviewCoverageLedger coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        Write(new ReviewTraceRecord
        {
            EventType = "coverage_created",
            PathCount = coverage.Entries.Count,
            ManifestSelectedCount = coverage.SelectedCount,
            ManifestReviewableCount = coverage.DeepReviewedCount + coverage.MandatoryChangesReviewedCount,
            ManifestExcludedCount = coverage.ExcludedCount,
            Outcome = coverage.CanAdvanceBaseline ? "Complete" : "Incomplete",
            ReasonCode = coverage.Strategy.ToString()
        });
    }

    /// <summary>Creates an observer that adds model and review-unit context to repository read events</summary>
    public Action<RepositoryReadAuditEvent> CreateReadObserver(string modelName, string profileName, ReviewTraceContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(context);

        return auditEvent =>
        {
            lock (_sync)
            {
                _readRequestCount++;
                switch (auditEvent.Outcome)
                {
                    case RepositoryReadOutcome.Served:
                        _servedReadCount++;
                        break;

                    case RepositoryReadOutcome.PartiallyServed:
                        _partiallyServedReadCount++;
                        break;

                    case RepositoryReadOutcome.Rejected:
                        _rejectedReadCount++;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(auditEvent), auditEvent.Outcome, "Unknown repository read outcome");
                }
            }

            Write(new ReviewTraceRecord
            {
                EventType = "repository_read",
                ModelName = modelName,
                ProfileName = profileName,
                UnitId = context.UnitId,
                ToolName = ReadFileLinesTool.ToolName,
                RequestedPath = Bound(auditEvent.RequestedPath, MaxRecordedPathCharacters),
                RequestedPathCharacters = auditEvent.RequestedPath?.Length,
                RequestedPathTruncated = auditEvent.RequestedPath?.Length > MaxRecordedPathCharacters,
                NormalizedPath = auditEvent.NormalizedPath,
                RequestedStartLine = auditEvent.RequestedStartLine,
                RequestedEndLine = auditEvent.RequestedEndLine,
                ReturnedStartLine = auditEvent.ReturnedStartLine,
                ReturnedEndLine = auditEvent.ReturnedEndLine,
                ReturnedLineCount = auditEvent.ReturnedLineCount,
                ReturnedContentCharacters = auditEvent.ReturnedContentCharacters,
                ResponseCharacters = auditEvent.ResponseCharacters,
                TotalLines = auditEvent.TotalLines,
                Outcome = auditEvent.Outcome.ToString(),
                ReasonCode = auditEvent.ReasonCode,
                NextStartLine = auditEvent.NextStartLine,
                DurationMilliseconds = auditEvent.DurationMilliseconds
            });
        };
    }

    /// <summary>Creates an observer that records controller-selected source ranges without storing source or prompt text</summary>
    public Action<ReviewContextSelectionEvent> CreateContextSelectionObserver(string modelName, string profileName, ReviewTraceContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(context);

        return selection => Write(new ReviewTraceRecord
        {
            EventType = "context_selected",
            ModelName = modelName,
            ProfileName = profileName,
            UnitId = context.UnitId,
            NormalizedPath = selection.Path,
            ReturnedStartLine = selection.StartLine,
            ReturnedEndLine = selection.EndLine,
            ReturnedLineCount = selection.LineCount,
            ReturnedContentCharacters = selection.ContentCharacters
        });
    }

    /// <summary>Creates an observer that records generic tool dispatch without storing argument or result bodies</summary>
    public Action<ModelToolCallAuditEvent> CreateToolObserver(string modelName, string profileName, ReviewTraceContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(context);

        return auditEvent =>
        {
            lock (_sync)
            {
                _toolCallEventCount++;
            }

            Write(new ReviewTraceRecord
            {
                EventType = "model_tool_call",
                ModelName = modelName,
                ProfileName = profileName,
                UnitId = context.UnitId,
                ToolName = Bound(auditEvent.ToolName, MaxRecordedToolNameCharacters),
                Outcome = auditEvent.Outcome.ToString(),
                ArgumentsCharacters = auditEvent.ArgumentsCharacters,
                ResponseCharacters = auditEvent.ResultCharacters,
                DurationMilliseconds = auditEvent.DurationMilliseconds
            });
        };
    }

    /// <inheritdoc />
    public void Dispose() => _writer.Dispose();

    private void Write(ReviewTraceRecord traceRecord)
    {
        lock (_sync)
        {
            ReviewTraceRecord sequenced = traceRecord with
            {
                Sequence = ++_sequence,
                TimestampUtc = _timeProvider.GetUtcNow()
            };
            _writer.WriteLine(JsonSerializer.Serialize(sequenced, JsonOptions));
        }
    }

    private static string? Bound(string? value, int maxCharacters) => value is null || value.Length <= maxCharacters ? value : value[..maxCharacters];
}
