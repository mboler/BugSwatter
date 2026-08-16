using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Informant;

/// <summary>Final operator-facing disposition of one structurally identified finding</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingDisposition>))]
public enum FindingDisposition
{
    /// <summary>A finding absent from the accepted ledger and without an applicable inline suppression</summary>
    New,

    /// <summary>A finding present in the accepted ledger</summary>
    Known,

    /// <summary>A finding covered by a justified inline suppression marker</summary>
    Suppressed
}

/// <summary>Relationship between one finding and the optional second-opinion pass</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingValidatorStatus>))]
public enum FindingValidatorStatus
{
    /// <summary>No second opinion was configured for the finding</summary>
    NotRun,

    /// <summary>The second opinion confirmed the same structural finding</summary>
    Confirmed,

    /// <summary>The second opinion explicitly discarded the same structural finding</summary>
    Discarded,

    /// <summary>The second opinion completed for the file but did not structurally resolve this finding</summary>
    Unresolved,

    /// <summary>The second-opinion pass or file validation did not complete</summary>
    Incomplete
}

/// <summary>Model pass that first supplied one artifact finding</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingOrigin>))]
public enum FindingOrigin
{
    /// <summary>The primary review model</summary>
    Primary,

    /// <summary>The optional second-opinion model</summary>
    SecondOpinion
}

/// <summary>One finding recorded in a managed per-run artifact</summary>
public sealed record FindingArtifactEntry(string Fingerprint, int FingerprintVersion, FindingOrigin Origin, string ReviewFile, string Path, int ReportedLine, int AnchorLine, string Category,
    string Severity, string Summary, string PreviousAnchor, string SourceAnchor, string NextAnchor, FindingDisposition Disposition, FindingValidatorStatus ValidatorStatus,
    DateTimeOffset? AcceptedAtUtc = null, string? SuppressionJustification = null, int? SuppressionMarkerLine = null);

/// <summary>Machine-readable finding intelligence for one completed review run</summary>
public sealed record FindingRunArtifact(int Version, string RepositoryUrl, string Branch, string TipSha, DateTimeOffset CreatedUtc, IReadOnlyList<FindingArtifactEntry> Findings,
    int AcceptedLedgerCount, int InvalidSuppressionMarkerCount, int StaleSuppressionCount);

/// <summary>One accepted structural finding retained across review runs</summary>
public sealed record AcceptedFindingState(string RepositoryUrl, string Branch, string Fingerprint, DateTimeOffset AcceptedAtUtc, string Path, int AnchorLine, string Category, string Summary);

/// <summary>First and latest observations of one inline suppression</summary>
public sealed record FindingSuppressionState(string RepositoryUrl, string Branch, string Fingerprint, DateTimeOffset FirstObservedUtc, DateTimeOffset LastObservedUtc, string Path, int AnchorLine,
    int MarkerLine, string Justification);

/// <summary>Repository-scoped accepted and suppressed state used during one run</summary>
public sealed record FindingStateSnapshot(IReadOnlyList<AcceptedFindingState> Accepted, IReadOnlyList<FindingSuppressionState> Suppressions)
{
    /// <summary>Finds an accepted record by exact structural fingerprint</summary>
    public AcceptedFindingState? FindAccepted(string fingerprint) => Accepted.FirstOrDefault(entry => string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal));

    /// <summary>Finds a suppression observation by exact structural fingerprint</summary>
    public FindingSuppressionState? FindSuppression(string fingerprint) => Suppressions.FirstOrDefault(entry => string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal));
}

/// <summary>Persists accepted findings and suppression age separately from the disposable review clone</summary>
public sealed class FindingStateStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a finding-state store at the configured app-local path</summary>
    public FindingStateStore(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Loads accepted and suppression state for one exact repository and branch</summary>
    public FindingStateSnapshot LoadSnapshot(string repositoryUrl, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        FindingStateDocument document = Load();
        return new FindingStateSnapshot(
            [.. document.Accepted.Where(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch)],
            [.. document.Suppressions.Where(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch)]);
    }

    /// <summary>Records current justified suppressions and returns the refreshed repository snapshot</summary>
    public FindingStateSnapshot ObserveSuppressions(string repositoryUrl, string branch, IEnumerable<FindingArtifactEntry> suppressedFindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(suppressedFindings);

        FindingStateDocument document = Load();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool changed = false;
        foreach (FindingArtifactEntry finding in suppressedFindings.Where(finding => finding.Disposition == FindingDisposition.Suppressed))
        {
            int index = document.Suppressions.FindIndex(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch && entry.Fingerprint == finding.Fingerprint);
            if (index >= 0)
            {
                FindingSuppressionState existing = document.Suppressions[index];
                document.Suppressions[index] = existing with
                {
                    LastObservedUtc = now,
                    Path = finding.Path,
                    AnchorLine = finding.AnchorLine,
                    MarkerLine = finding.SuppressionMarkerLine ?? existing.MarkerLine,
                    Justification = finding.SuppressionJustification ?? existing.Justification
                };
            }
            else
            {
                document.Suppressions.Add(new FindingSuppressionState(repositoryUrl, branch, finding.Fingerprint, now, now, finding.Path, finding.AnchorLine,
                    finding.SuppressionMarkerLine ?? finding.AnchorLine, finding.SuppressionJustification ?? "justification unavailable"));
            }

            changed = true;
        }

        if (changed)
        {
            Save(document);
        }

        return new FindingStateSnapshot(
            [.. document.Accepted.Where(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch)],
            [.. document.Suppressions.Where(entry => entry.RepositoryUrl == repositoryUrl && entry.Branch == branch)]);
    }

    /// <summary>Adds every new finding from one run artifact to the accepted ledger and returns the number added</summary>
    public int AcceptNew(FindingRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Version != CurrentVersion)
        {
            throw new InformantFatalException($"Finding artifact version {artifact.Version} is not supported; expected {CurrentVersion}.");
        }

        FindingStateDocument document = Load();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var accepted = new HashSet<string>(document.Accepted.Where(entry => entry.RepositoryUrl == artifact.RepositoryUrl && entry.Branch == artifact.Branch).Select(entry => entry.Fingerprint),
            StringComparer.Ordinal);
        int added = 0;
        foreach (FindingArtifactEntry finding in artifact.Findings.Where(finding => finding.Disposition == FindingDisposition.New))
        {
            if (!accepted.Add(finding.Fingerprint))
            {
                continue;
            }

            document.Accepted.Add(new AcceptedFindingState(artifact.RepositoryUrl, artifact.Branch, finding.Fingerprint, now, finding.Path, finding.AnchorLine, finding.Category, finding.Summary));
            added++;
        }

        if (added > 0)
        {
            Save(document);
        }

        return added;
    }

    private FindingStateDocument Load()
    {
        if (!File.Exists(_path))
        {
            return new FindingStateDocument();
        }

        try
        {
            FindingStateDocument document = JsonSerializer.Deserialize<FindingStateDocument>(File.ReadAllText(_path), JsonOptions) ?? new FindingStateDocument();
            if (document.Version != CurrentVersion)
            {
                throw new InformantFatalException($"Finding state version {document.Version} is not supported; expected {CurrentVersion}.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new InformantFatalException($"Unable to read finding state from '{_path}': {ex.Message}", ex);
        }
    }

    private void Save(FindingStateDocument document)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class FindingStateDocument
    {
        public int Version { get; init; } = CurrentVersion;

        public List<AcceptedFindingState> Accepted { get; init; } = [];

        public List<FindingSuppressionState> Suppressions { get; init; } = [];
    }
}

/// <summary>Writes and locates managed per-run finding artifacts</summary>
public static class FindingArtifactFile
{
    private const int CurrentVersion = 1;
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";
    private const string Prefix = "Informant-Findings-";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Writes one artifact atomically and returns its absolute path</summary>
    public static string Write(string reportDirectory, string runStamp, FindingRunArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runStamp);
        ArgumentNullException.ThrowIfNull(artifact);

        Directory.CreateDirectory(reportDirectory);
        string path = Path.GetFullPath(Path.Combine(reportDirectory, $"{Prefix}{runStamp}.json"));
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(artifact, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return path;
    }

    /// <summary>Loads the newest exact managed artifact for the configured repository and branch</summary>
    public static (string Path, FindingRunArtifact Artifact) LoadLatest(string reportDirectory, string repositoryUrl, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        if (!Directory.Exists(reportDirectory))
        {
            throw new InformantFatalException($"No finding artifact exists in {Path.GetFullPath(reportDirectory)}.");
        }

        foreach (string path in Directory.EnumerateFiles(reportDirectory, $"{Prefix}*.json", SearchOption.TopDirectoryOnly).OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || !HasManagedName(file.Name))
            {
                continue;
            }

            FindingRunArtifact artifact;
            try
            {
                artifact = JsonSerializer.Deserialize<FindingRunArtifact>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InformantFatalException($"Finding artifact {path} was empty.");
            }
            catch (JsonException ex)
            {
                throw new InformantFatalException($"Unable to read finding artifact '{path}': {ex.Message}", ex);
            }

            if (artifact.Version != CurrentVersion)
            {
                throw new InformantFatalException($"Finding artifact version {artifact.Version} in '{path}' is not supported; expected {CurrentVersion}.");
            }

            if (artifact.RepositoryUrl == repositoryUrl && artifact.Branch == branch)
            {
                return (Path.GetFullPath(path), artifact);
            }
        }

        throw new InformantFatalException($"No finding artifact for repository {repositoryUrl} and branch {branch} exists in {Path.GetFullPath(reportDirectory)}.");
    }

    private static bool HasManagedName(string fileName)
    {
        string timestamp = fileName.Length == Prefix.Length + TimestampFormat.Length + 5 ? fileName.Substring(Prefix.Length, TimestampFormat.Length) : "";
        return fileName.EndsWith(".json", StringComparison.Ordinal)
            && DateTime.TryParseExact(timestamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
