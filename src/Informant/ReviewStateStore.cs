using System.Text.Json;

namespace Informant;

/// <summary>One recorded baseline: the last tip SHA reviewed for a repository and branch</summary>
public sealed record BaselineEntry(string Sha, DateTimeOffset UpdatedUtc);

/// <summary>Persists last-reviewed baseline SHAs keyed by repository plus branch, so adding more repositories or branches later needs no format change</summary>
public sealed class ReviewStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<string, BaselineEntry> _baselines;

    /// <summary>Loads existing state from <paramref name="path"/>, or starts empty when the file does not exist</summary>
    public ReviewStateStore(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _path = path;

        if (!File.Exists(path))
        {
            _baselines = [];
            return;
        }

        try
        {
            _baselines = JsonSerializer.Deserialize<Dictionary<string, BaselineEntry>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InformantFatalException($"State file {path} is corrupt: {ex.Message}. Delete it to start over; the next run will then review everything", ex);
        }
    }

    /// <summary>Returns the baseline SHA for the repository and branch, or null when never reviewed</summary>
    public string? GetBaseline(string repositoryUrl, string branch) => _baselines.TryGetValue(Key(repositoryUrl, branch), out BaselineEntry? entry) ? entry.Sha : null;

    /// <summary>Records the newly reviewed tip SHA and writes the state file</summary>
    public void SetBaseline(string repositoryUrl, string branch, string sha)
    {
        _baselines[Key(repositoryUrl, branch)] = new BaselineEntry(sha, DateTimeOffset.UtcNow);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-then-move so a crash mid-write cannot corrupt the state file and brick every later unattended run
        string tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_baselines, JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }

    private static string Key(string repositoryUrl, string branch) => $"{repositoryUrl}|{branch}";
}
