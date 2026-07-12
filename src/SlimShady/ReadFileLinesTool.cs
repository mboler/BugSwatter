using System.Text;
using System.Text.Json;
using Serilog;

namespace SlimShady;

/// <summary>The single, read-only tool exposed to the model. It returns a numbered line range from a file inside the allowed read-root and nothing else; no write, delete, execute or directory-mutation capability exists here. Every failure returns a structured error string for the model to recover from, never an exception</summary>
public sealed class ReadFileLinesTool
{
    /// <summary>Tool name declared to the model</summary>
    public const string ToolName = "read_file_lines";

    /// <summary>Upper bound on lines returned per call, keeping any single tool result inside the context budget</summary>
    public const int MaxLinesPerCall = 400;

    /// <summary>Tool declaration sent to the model with every request</summary>
    public static readonly ToolDefinition Definition = new()
    {
        Function = new FunctionDefinition
        {
            Name = ToolName,
            Description = "Read a range of lines from a source file in the repository, to pull additional context for the review. Returns the requested lines prefixed with their line numbers.",
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

    private readonly string _rootWithSeparator;
    private readonly StringComparison _pathComparison;

    /// <summary>Creates the tool confined to <paramref name="allowedReadRoot"/></summary>
    public ReadFileLinesTool(string allowedReadRoot)
    {
        ArgumentNullException.ThrowIfNull(allowedReadRoot);

        _rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedReadRoot)) + Path.DirectorySeparatorChar;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>Parses the raw JSON arguments of a tool call and executes the read; malformed arguments produce a structured error result</summary>
    public string ExecuteRaw(string argumentsJson)
    {
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
            return Error($"invalid arguments: {ex.Message}. Expected {{\"path\": string, \"start_line\": int, \"end_line\": int}}");
        }

        return Execute(path, startLine, endLine);
    }

    /// <summary>Executes a validated read: confines the path to the read-root, checks the range, and returns numbered lines</summary>
    /// <param name="path">File path relative to the read root; forward or backward slashes both work, and anything resolving outside the root is refused</param>
    /// <returns>Numbered source lines on success, or a JSON error object the model can read and recover from</returns>
    public string Execute(string path, int startLine, int endLine)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("path is required");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_rootWithSeparator, path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Error($"invalid path '{path}': {ex.Message}");
        }

        if (!fullPath.StartsWith(_rootWithSeparator, _pathComparison))
        {
            Log.Warning("read_file_lines rejected path outside the read root: {Path}", path);
            return Error($"path '{path}' resolves outside the allowed read root; only files inside the repository can be read");
        }

        if (!File.Exists(fullPath))
        {
            return Error($"file not found: {path}");
        }

        if (startLine < 1)
        {
            return Error($"start_line must be 1 or greater, got {startLine}");
        }

        if (endLine < startLine)
        {
            return Error($"end_line ({endLine}) must be greater than or equal to start_line ({startLine})");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error($"could not read '{path}': {ex.Message}");
        }

        if (Array.Exists(lines, line => line.Contains('\0')))
        {
            return Error($"'{path}' appears to be a binary file and cannot be read as text");
        }

        if (startLine > lines.Length)
        {
            return Error($"start_line {startLine} is beyond the end of the file; '{path}' has {lines.Length} lines");
        }

        int effectiveEnd = Math.Min(endLine, lines.Length);
        bool capped = effectiveEnd - startLine + 1 > MaxLinesPerCall;
        if (capped)
        {
            effectiveEnd = startLine + MaxLinesPerCall - 1;
        }

        var builder = new StringBuilder();
        if (effectiveEnd != endLine)
        {
            string cappedNote = capped ? $"; at most {MaxLinesPerCall} lines are returned per call, request further ranges as needed" : "";
            builder.AppendLine($"note: returning lines {startLine}-{effectiveEnd} of '{path}', which has {lines.Length} lines total{cappedNote}");
        }

        for (int lineNumber = startLine; lineNumber <= effectiveEnd; lineNumber++)
        {
            builder.AppendLine($"{lineNumber,6} | {lines[lineNumber - 1]}");
        }

        Log.Debug("read_file_lines served {Path} lines {Start}-{End}", path, startLine, effectiveEnd);
        return builder.ToString();
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
