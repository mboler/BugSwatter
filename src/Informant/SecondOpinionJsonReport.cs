using System.Text.Json;
using System.Text.Json.Serialization;

namespace Informant;

/// <summary>Result of a completed second-opinion pass, carried to the email step</summary>
public sealed record SecondOpinionOutcome(string ValidatedReportPath, string ValidatedJsonPath, Severity MaxSeverity, bool SeverityDetermined, int ValidatedCount, int FailedCount);

/// <summary>Accumulates the structured second-opinion verdicts across a run and writes the machine-readable companion artifact next to the validated Markdown report. Files whose json did not parse are recorded with parseOk false so a consumer can tell confirmed-none from could-not-parse</summary>
public sealed class SecondOpinionJsonReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };

    private readonly List<FileValidation> _files = [];

    /// <summary>Highest confirmed severity seen across every parsed file, for the email gate</summary>
    public Severity MaxSeverity { get; private set; } = Severity.None;

    /// <summary>True only when every completed validation returned parseable structured findings</summary>
    public bool SeverityDetermined => _files.All(file => file.ParseOk);

    /// <summary>Records one file's validation; <paramref name="parsed"/> is null when the model produced no usable json</summary>
    public void Add(string filePath, IReadOnlyList<LineRange> ranges, ParsedValidation? parsed)
    {
        string rangeText = ranges.Count == 0 ? "(entire file)" : string.Join(", ", ranges.Select(range => range.ToString()));

        if (parsed is null)
        {
            _files.Add(new FileValidation(filePath, rangeText, false, [], [], null));
            return;
        }

        _files.Add(new FileValidation(filePath, rangeText, true, parsed.Confirmed, parsed.Discarded, parsed.Verdict));

        Severity fileMax = SecondOpinionParser.MaxSeverity(parsed.Confirmed);
        if (fileMax > MaxSeverity)
        {
            MaxSeverity = fileMax;
        }
    }

    /// <summary>Writes the companion json artifact and returns its path</summary>
    public string Write(string directory, string runStamp, string validatingModel, string endpoint, string sourceReportPath)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"Informant-Report-{runStamp}-validated.json");

        var document = new
        {
            validatingModel,
            endpoint,
            sourceReport = Path.GetFileName(sourceReportPath),
            maxSeverity = SeverityDetermined ? MaxSeverity.ToString() : "Undetermined",
            severityDetermined = SeverityDetermined,
            fileCount = _files.Count,
            files = _files
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));

        return path;
    }

    private sealed record FileValidation(string File, string ChangedRanges, bool ParseOk, IReadOnlyList<ConfirmedFinding> Confirmed, IReadOnlyList<DiscardedFinding> Discarded, string? Verdict);
}
