using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimShady;

/// <summary>Persists the detected change set to a timestamped JSON file so every run's review input is inspectable after the fact</summary>
public static class ChangeSetFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Writes the change set and returns the file path</summary>
    public static string Write(string reportDirectory, string runStamp, string? baselineSha, string tipSha, ReviewMode mode, IReadOnlyList<ChangedFile> files)
    {
        Directory.CreateDirectory(reportDirectory);
        string path = Path.Combine(reportDirectory, $"SlimShady-Changes-{runStamp}.json");
        var document = new { generated = DateTimeOffset.Now, baselineSha, tipSha, mode = mode.ToString(), fileCount = files.Count, files };
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
        
        return path;
    }
}
