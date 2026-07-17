using System.Text.Json;
using BugSwatter.Common;
using Serilog;

namespace Marshal;

/// <summary>One completed run recorded for the dashboard and status views</summary>
public sealed record HistoryEntry
{
    /// <summary>Job name</summary>
    public string Job { get; init; } = "";

    /// <summary>What triggered the run</summary>
    public string Trigger { get; init; } = "";

    /// <summary>When the run started, UTC ISO-8601</summary>
    public string StartedUtc { get; init; } = "";

    /// <summary>How long the run took, in seconds</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Child exit code, or null when the process did not produce one</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether the run was killed for exceeding the timeout</summary>
    public bool TimedOut { get; init; }

    /// <summary>Overall outcome: completed, failed, timed-out or aborted</summary>
    public string Outcome { get; init; } = "";

    /// <summary>Report file path when one was discovered</summary>
    public string? ReportPath { get; init; }

    /// <summary>Highest confirmed severity from the second opinion when available, otherwise null</summary>
    public string? MaxSeverity { get; init; }

    /// <summary>Final usage across every model request in the run, or null for history written before scoped usage was available</summary>
    public ReviewUsageSnapshot? RunUsage { get; init; }

    /// <summary>Final local-model usage, or null for history written before scoped usage was available</summary>
    public ReviewUsageSnapshot? LocalUsage { get; init; }

    /// <summary>Final frontier-model usage and estimated cost, or null for history written before scoped usage was available</summary>
    public ReviewUsageSnapshot? FrontierUsage { get; init; }
}

/// <summary>Append-only JSON-lines history of completed runs. Writes are best-effort: a failure to record history must never disturb a finished run. Reads power the dashboard and status views</summary>
public sealed class RunHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly string _path;

    /// <summary>Creates a store backed by the given jsonl file</summary>
    public RunHistoryStore(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = path;
    }

    /// <summary>Appends one entry; a write failure is logged, never thrown</summary>
    public void Append(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;

            lock (_gate)
            {
                File.AppendAllText(_path, line);
            }
        }
        catch (Exception ex)
        {
            // catch-all: history is a courtesy record; failing to write it must not disturb the completed run
            Log.Warning("Could not append run history to {Path}: {Reason}", _path, ex.Message);
        }
    }

    /// <summary>Returns the most recent entries, newest first, up to <paramref name="max"/>; an unreadable or missing file yields an empty list</summary>
    public IReadOnlyList<HistoryEntry> ReadRecent(int max)
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                {
                    return [];
                }

                return [.. File.ReadLines(_path)
                    .Reverse()
                    .Where(line => line.Trim().Length > 0)
                    .Take(max)
                    .Select(TryDeserialize)
                    .OfType<HistoryEntry>()];
            }
        }
        catch (Exception ex)
        {
            // catch-all: a corrupt or locked history file must not take down the dashboard
            Log.Warning("Could not read run history from {Path}: {Reason}", _path, ex.Message);
            return [];
        }
    }

    private static HistoryEntry? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the maxSeverity from the validated-json companion beside a report, or null when there is none</summary>
    public static string? TryReadMaxSeverity(string? reportPath)
    {
        if (reportPath is null || !reportPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The companion JSON is <stamp>-validated.json. Marshal discovers the newest report, which after a
        // second-opinion run is the validated report itself (<stamp>-validated.md), so derive the same
        // companion whether the discovered path is the local report or the validated one
        string companion = reportPath.EndsWith("-validated.md", StringComparison.OrdinalIgnoreCase)
            ? reportPath[..^3] + ".json"
            : reportPath[..^3] + "-validated.json";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(companion));
            return document.RootElement.TryGetProperty("maxSeverity", out JsonElement element) ? element.GetString() : null;
        }
        catch (Exception)
        {
            // catch-all: no companion, or unreadable, simply means severity is unknown for this run
            return null;
        }
    }
}
