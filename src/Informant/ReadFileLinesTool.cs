using System.Text;
using System.Text.Json;
using Serilog;

namespace Informant;

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

    private readonly RepositoryFileReader _reader;

    /// <summary>Creates the tool confined to <paramref name="allowedReadRoot"/></summary>
    public ReadFileLinesTool(string allowedReadRoot, int maxFileBytes = RepositoryFileReader.DefaultMaxFileBytes)
    {
        ArgumentNullException.ThrowIfNull(allowedReadRoot);

        _reader = new RepositoryFileReader(allowedReadRoot, maxFileBytes);
    }

    /// <summary>Creates the tool over an existing bounded repository reader</summary>
    public ReadFileLinesTool(RepositoryFileReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
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

        if (startLine < 1)
        {
            return Error($"start_line must be 1 or greater, got {startLine}");
        }

        if (endLine < startLine)
        {
            return Error($"end_line ({endLine}) must be greater than or equal to start_line ({startLine})");
        }

        RepositoryLineRange result;
        try
        {
            result = _reader.ReadLines(path, startLine, endLine, MaxLinesPerCall);
        }
        catch (RepositoryFileException ex)
        {
            if (ex.Error is RepositoryFileError.OutsideRoot or RepositoryFileError.InvalidPath or RepositoryFileError.ReparsePoint)
            {
                Log.Warning("read_file_lines rejected unsafe path {Path}: {Reason}", path, ex.Message);
            }

            return Error(ex.Message);
        }

        if (startLine > result.TotalLines)
        {
            return Error($"start_line {startLine} is beyond the end of the file; '{path}' has {result.TotalLines} lines");
        }

        var builder = new StringBuilder();
        if (result.EffectiveEndLine != endLine)
        {
            string cappedNote = result.Capped ? $"; at most {MaxLinesPerCall} lines are returned per call, request further ranges as needed" : "";
            builder.AppendLine($"note: returning lines {startLine}-{result.EffectiveEndLine} of '{path}', which has {result.TotalLines} lines total{cappedNote}");
        }

        foreach ((int lineNumber, string text) in result.Lines)
        {
            builder.AppendLine($"{lineNumber,6} | {text}");
        }

        Log.Debug("read_file_lines served {Path} lines {Start}-{End}", path, startLine, result.EffectiveEndLine);
        return builder.ToString();
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
