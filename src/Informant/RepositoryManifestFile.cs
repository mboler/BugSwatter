using System.Text.Json;
using System.Text.Json.Serialization;

namespace Informant;

/// <summary>Persists a repository manifest as a timestamped JSON artifact</summary>
public static class RepositoryManifestFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Writes the manifest and returns the artifact path</summary>
    public static string Write(string reportDirectory, string runStamp, RepositoryManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runStamp);
        ArgumentNullException.ThrowIfNull(manifest);

        Directory.CreateDirectory(reportDirectory);
        string path = Path.Combine(reportDirectory, $"Informant-Manifest-{runStamp}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }
}
