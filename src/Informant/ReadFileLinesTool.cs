using System.Diagnostics;
using System.Text.Json;
using BugSwatter.AI;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Disposition of one model-requested repository read</summary>
public enum RepositoryReadOutcome
{
    /// <summary>The complete available request range was returned</summary>
    Served,

    /// <summary>A safe prefix was returned with explicit continuation metadata</summary>
    PartiallyServed,

    /// <summary>No source content was returned</summary>
    Rejected
}

/// <summary>Metadata-only audit event for one model-requested repository read</summary>
public sealed record RepositoryReadAuditEvent(string? RequestedPath, string? NormalizedPath, int? RequestedStartLine, int? RequestedEndLine, RepositoryReadOutcome Outcome,
    int? ReturnedStartLine, int? ReturnedEndLine, int ReturnedLineCount, int ReturnedContentCharacters, int ResponseCharacters, int? TotalLines, string? ReasonCode, int? NextStartLine,
    long DurationMilliseconds);

/// <summary>The single read-only tool exposed to a model, confined by live path checks, the current manifest, and response bounds</summary>
public sealed class ReadFileLinesTool : IModelTool
{
    /// <summary>Tool name declared to the model</summary>
    public const string ToolName = "read_file_lines";

    /// <summary>Upper bound on lines returned per call</summary>
    public const int MaxLinesPerCall = 400;

    /// <summary>Default upper bound on the complete serialized tool response</summary>
    public const int DefaultMaxResultCharacters = 6000;

    /// <summary>Smallest useful serialized response budget</summary>
    public const int MinimumMaxResultCharacters = 512;

    /// <summary>Tool declaration sent to the model with every request</summary>
    public static readonly ToolDefinition Definition = new()
    {
        Function = new FunctionDefinition
        {
            Name = ToolName,
            Description = "Read a bounded line range from a text file in the current repository manifest. The JSON result says whether the range is complete or partial and how to continue.",
            Parameters = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "File path relative to the repository root, forward slashes preferred" },
                    "start_line": { "type": "integer", "description": "First line to read, 1-based" },
                    "end_line": { "type": "integer", "description": "Last line to read, inclusive" }
                  },
                  "required": ["path", "start_line", "end_line"]
                }
                """)
        }
    };

    private const int MaxRepeatedRejectedRequests = 3;
    private const int MaxResponseMessageCharacters = 256;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RepositoryFileReader _reader;
    private readonly RepositoryReadAllowlist? _allowlist;
    private readonly int _maxResultCharacters;
    private readonly Action<RepositoryReadAuditEvent>? _auditObserver;
    private string? _lastRejectedRequestKey;
    private int _consecutiveRejectedRequestCount;

    /// <summary>Creates the tool confined to <paramref name="allowedReadRoot"/></summary>
    public ReadFileLinesTool(string allowedReadRoot, int maxFileBytes = RepositoryFileReader.DefaultMaxFileBytes, RepositoryManifest? manifest = null,
        int maxResultCharacters = DefaultMaxResultCharacters, Action<RepositoryReadAuditEvent>? auditObserver = null)
        : this(new RepositoryFileReader(allowedReadRoot, maxFileBytes), manifest, maxResultCharacters, auditObserver)
    {
    }

    /// <summary>Creates the tool over an existing bounded repository reader</summary>
    public ReadFileLinesTool(RepositoryFileReader reader, RepositoryManifest? manifest = null, int maxResultCharacters = DefaultMaxResultCharacters,
        Action<RepositoryReadAuditEvent>? auditObserver = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxResultCharacters < MinimumMaxResultCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResultCharacters), maxResultCharacters, $"Result character limit must be at least {MinimumMaxResultCharacters}");
        }

        _reader = reader;
        _allowlist = manifest is null ? null : new RepositoryReadAllowlist(manifest, reader.Root);
        _maxResultCharacters = maxResultCharacters;
        _auditObserver = auditObserver;
    }

    ToolDefinition IModelTool.Definition => Definition;

    /// <summary>Calculates the per-call response budget reserved from a configured conversation budget</summary>
    public static int ResultCharactersForContext(int maxContextCharacters)
    {
        if (maxContextCharacters < MinimumMaxResultCharacters * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContextCharacters), maxContextCharacters, $"Context character limit must be at least {MinimumMaxResultCharacters * 4}");
        }

        return Math.Min(DefaultMaxResultCharacters, maxContextCharacters / 4);
    }

    /// <summary>Parses raw JSON arguments and executes the bounded read</summary>
    public string ExecuteRaw(string argumentsJson)
    {
        long started = Stopwatch.GetTimestamp();
        string path;
        int startLine;
        int endLine;

        try
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            path = arguments.RootElement.GetProperty("path").GetString() ?? "";
            startLine = arguments.RootElement.GetProperty("start_line").GetInt32();
            endLine = arguments.RootElement.GetProperty("end_line").GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Reject(null, null, null, null, "InvalidArguments", $"invalid arguments: {ex.Message}", started, countRepeatedRequest: false);
        }

        return Execute(path, startLine, endLine, started);
    }

    string IModelTool.Execute(string argumentsJson) => ExecuteRaw(argumentsJson);

    /// <summary>Executes a validated, bounded read and returns a structured complete, partial, or rejected result</summary>
    public string Execute(string path, int startLine, int endLine) => Execute(path, startLine, endLine, Stopwatch.GetTimestamp());

    private string Execute(string path, int startLine, int endLine, long started)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Reject(path, null, startLine, endLine, "InvalidPath", "path is required", started);
        }

        if (startLine < 1)
        {
            return Reject(path, null, startLine, endLine, "InvalidRange", $"start_line must be 1 or greater, got {startLine}", started);
        }

        if (endLine < startLine)
        {
            return Reject(path, null, startLine, endLine, "InvalidRange", $"end_line ({endLine}) must be greater than or equal to start_line ({startLine})", started);
        }

        RepositoryReadAuthorization? authorization = null;
        if (_allowlist is not null)
        {
            authorization = _allowlist.Authorize(path);
            if (!authorization.Allowed)
            {
                return Reject(path, authorization.NormalizedPath, startLine, endLine, authorization.ReasonCode ?? "Rejected", authorization.Message ?? "read was rejected", started);
            }
        }

        string readPath = authorization?.ReadPath ?? path;
        RepositoryLineRange range;
        try
        {
            range = _reader.ReadLines(readPath, startLine, endLine, MaxLinesPerCall);
        }
        catch (RepositoryFileException ex)
        {
            if (ex.Error is RepositoryFileError.OutsideRoot or RepositoryFileError.InvalidPath or RepositoryFileError.ReparsePoint)
            {
                Log.Warning("read_file_lines rejected unsafe path {Path}: {Reason}", path, ex.Message);
            }

            return Reject(path, authorization?.NormalizedPath, startLine, endLine, ex.Error.ToString(), ex.Message, started);
        }

        if (authorization?.Entry is { } entry && !RepositoryReadAllowlist.MatchesSnapshot(entry, range))
        {
            return Reject(path, authorization.NormalizedPath, startLine, endLine, "ChangedSinceManifest", "file metadata or content changed after the run manifest was built", started);
        }

        if (startLine > range.TotalLines)
        {
            return Reject(path, authorization?.NormalizedPath, startLine, endLine, "RangeBeyondEnd", $"start_line {startLine} is beyond the end of the file, which has {range.TotalLines} lines", started);
        }

        string normalizedPath = authorization?.NormalizedPath ?? path.Replace('\\', '/');
        BuiltReadResponse? response = BuildResponse(normalizedPath, startLine, endLine, range);
        if (response is null)
        {
            return Reject(path, normalizedPath, startLine, endLine, "CharacterLimit", $"no complete source line fits within the {_maxResultCharacters}-character tool response limit", started);
        }

        RepositoryReadOutcome outcome = response.Partial ? RepositoryReadOutcome.PartiallyServed : RepositoryReadOutcome.Served;
        _lastRejectedRequestKey = null;
        _consecutiveRejectedRequestCount = 0;
        Observe(new RepositoryReadAuditEvent(path, normalizedPath, startLine, endLine, outcome, response.ReturnedStartLine, response.ReturnedEndLine, response.ReturnedLineCount,
            response.ReturnedContentCharacters, response.Json.Length, range.TotalLines, response.TruncationReason, response.NextStartLine, ElapsedMilliseconds(started)));
        Log.Debug("read_file_lines {Outcome} {Path} lines {Start}-{End}", outcome, normalizedPath, response.ReturnedStartLine, response.ReturnedEndLine);
        return response.Json;
    }

    private BuiltReadResponse? BuildResponse(string normalizedPath, int requestedStartLine, int requestedEndLine, RepositoryLineRange range)
    {
        string[] formattedLines = [.. range.Lines.Select(line => $"{line.Number,6} | {line.Text}")];
        int availableEndLine = Math.Min(requestedEndLine, range.TotalLines);
        bool lineLimited = range.EffectiveEndLine < availableEndLine;
        string? naturalReason = lineLimited ? "LineLimit" : requestedEndLine > range.TotalLines ? "EndOfFile" : null;
        int? naturalNextStartLine = lineLimited ? range.EffectiveEndLine + 1 : null;

        BuiltReadResponse complete = SerializeResponse(normalizedPath, requestedStartLine, requestedEndLine, formattedLines, !lineLimited, naturalReason, naturalNextStartLine, range.TotalLines);
        if (complete.Json.Length <= _maxResultCharacters)
        {
            return complete;
        }

        for (int lineCount = formattedLines.Length - 1; lineCount >= 1; lineCount--)
        {
            string[] boundedLines = formattedLines[..lineCount];
            int nextStartLine = range.Lines[lineCount - 1].Number + 1;
            BuiltReadResponse partial = SerializeResponse(normalizedPath, requestedStartLine, requestedEndLine, boundedLines, false, "CharacterLimit", nextStartLine, range.TotalLines);
            if (partial.Json.Length <= _maxResultCharacters)
            {
                return partial;
            }
        }

        return null;
    }

    private static BuiltReadResponse SerializeResponse(string path, int requestedStartLine, int requestedEndLine, IReadOnlyList<string> lines, bool complete, string? truncationReason,
        int? nextStartLine, int totalLines)
    {
        string content = string.Join('\n', lines);
        int? returnedStartLine = lines.Count == 0 ? null : ParseLineNumber(lines[0]);
        int? returnedEndLine = lines.Count == 0 ? null : ParseLineNumber(lines[^1]);
        var document = new ReadResponse(complete ? "complete" : "partial", path, requestedStartLine, requestedEndLine, returnedStartLine, returnedEndLine, totalLines, lines.Count,
            content.Length, complete, truncationReason, nextStartLine, content);
        string json = JsonSerializer.Serialize(document, JsonOptions);
        return new BuiltReadResponse(json, !complete, returnedStartLine, returnedEndLine, lines.Count, content.Length, truncationReason, nextStartLine);
    }

    private string Reject(string? requestedPath, string? normalizedPath, int? startLine, int? endLine, string reasonCode, string message, long started, bool countRepeatedRequest = true)
    {
        if (countRepeatedRequest)
        {
            string key = $"{requestedPath}\0{startLine}\0{endLine}";
            if (string.Equals(key, _lastRejectedRequestKey, StringComparison.Ordinal))
            {
                _consecutiveRejectedRequestCount++;
            }
            else
            {
                _lastRejectedRequestKey = key;
                _consecutiveRejectedRequestCount = 1;
            }

            if (_consecutiveRejectedRequestCount >= MaxRepeatedRejectedRequests)
            {
                reasonCode = "RepeatedRejectedRequest";
                message = "the same rejected request reached its retry limit, choose a different valid manifest path or range";
            }
        }

        string response = SerializeRejection(requestedPath, startLine, endLine, reasonCode, message);
        Observe(new RepositoryReadAuditEvent(requestedPath, normalizedPath, startLine, endLine, RepositoryReadOutcome.Rejected, null, null, 0, 0, response.Length, null, reasonCode, null,
            ElapsedMilliseconds(started)));
        return response;
    }

    private string SerializeRejection(string? requestedPath, int? startLine, int? endLine, string reasonCode, string message)
    {
        string? boundedPath = Bound(requestedPath, MaxResponseMessageCharacters);
        string boundedMessage = Bound(message, MaxResponseMessageCharacters) ?? reasonCode;
        string json = JsonSerializer.Serialize(new RejectedResponse("rejected", boundedPath, startLine, endLine, reasonCode, boundedMessage), JsonOptions);
        if (json.Length <= _maxResultCharacters)
        {
            return json;
        }

        return JsonSerializer.Serialize(new RejectedResponse("rejected", null, startLine, endLine, reasonCode, reasonCode), JsonOptions);
    }

    private void Observe(RepositoryReadAuditEvent auditEvent)
    {
        try
        {
            _auditObserver?.Invoke(auditEvent);
        }
        catch (Exception ex)
        {
            // Catch-all: optional audit telemetry must never alter repository-read safety or the model result.
            Log.Warning("Repository read audit observer failed: {Reason}", ex.Message);
        }
    }

    private static string? Bound(string? value, int maxCharacters) => value is null || value.Length <= maxCharacters ? value : value[..maxCharacters];

    private static int ParseLineNumber(string formattedLine) => int.Parse(formattedLine.AsSpan(0, 6));

    private static long ElapsedMilliseconds(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private sealed record ReadResponse(string Status, string Path, int RequestedStartLine, int RequestedEndLine, int? ReturnedStartLine, int? ReturnedEndLine, int TotalLines,
        int ReturnedLineCount, int ReturnedContentCharacters, bool Complete, string? TruncationReason, int? NextStartLine, string Content);

    private sealed record RejectedResponse(string Status, string? Path, int? RequestedStartLine, int? RequestedEndLine, string ReasonCode, string Error);

    private sealed record BuiltReadResponse(string Json, bool Partial, int? ReturnedStartLine, int? ReturnedEndLine, int ReturnedLineCount, int ReturnedContentCharacters,
        string? TruncationReason, int? NextStartLine);
}
