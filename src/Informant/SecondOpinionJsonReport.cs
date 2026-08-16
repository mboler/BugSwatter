using System.Text.Json;
using System.Text.Json.Serialization;

namespace Informant;

/// <summary>Outcome recorded for one file in the second-opinion pass</summary>
public enum SecondOpinionValidationStatus
{
    /// <summary>The response contained parseable structured findings</summary>
    Validated,

    /// <summary>The model request failed before a response was available</summary>
    RequestFailed,

    /// <summary>The model returned no answer</summary>
    EmptyResponse,

    /// <summary>The answer did not contain parseable structured findings</summary>
    ParseFailed,

    /// <summary>The pass-level budget expired before this file could complete validation</summary>
    BudgetDeferred
}

/// <summary>Structured second-opinion result or explicit failure for one selected primary file</summary>
public sealed record SecondOpinionFileValidation(string File, string ChangedRanges, SecondOpinionValidationStatus Status, bool ParseOk, IReadOnlyList<ConfirmedFinding> Confirmed,
    IReadOnlyList<DiscardedFinding> Discarded, string? Verdict, string? FailureReason);

/// <summary>Result of a completed second-opinion pass, carried to the email step</summary>
public sealed record SecondOpinionOutcome(string ValidatedReportPath, string ValidatedJsonPath, Severity MaxSeverity, int ValidatedCount, int RequestFailureCount, int EmptyResponseCount,
    int ParseFailureCount, int BudgetDeferredCount = 0, IReadOnlyList<SecondOpinionFileValidation>? Validations = null)
{
    /// <summary>Total files whose second-opinion validation did not complete</summary>
    public int FailedCount => RequestFailureCount + EmptyResponseCount + ParseFailureCount + BudgetDeferredCount;

    /// <summary>True only when every attempted file produced parseable structured findings</summary>
    public bool SeverityDetermined => FailedCount == 0;
}

/// <summary>Accumulates structured second-opinion verdicts and explicit failure states across a run, then writes the machine-readable companion artifact next to the validated Markdown report</summary>
public sealed class SecondOpinionJsonReport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() } };

    private readonly List<SecondOpinionFileValidation> _files = [];

    /// <summary>Highest confirmed severity seen across every parsed file, for the email gate</summary>
    public Severity MaxSeverity { get; private set; } = Severity.None;

    /// <summary>Number of files with parseable structured findings</summary>
    public int ValidatedCount => _files.Count(file => file.Status == SecondOpinionValidationStatus.Validated);

    /// <summary>Number of files whose model request failed</summary>
    public int RequestFailureCount => _files.Count(file => file.Status == SecondOpinionValidationStatus.RequestFailed);

    /// <summary>Number of files whose model response was empty</summary>
    public int EmptyResponseCount => _files.Count(file => file.Status == SecondOpinionValidationStatus.EmptyResponse);

    /// <summary>Number of files whose model response could not be parsed</summary>
    public int ParseFailureCount => _files.Count(file => file.Status == SecondOpinionValidationStatus.ParseFailed);

    /// <summary>Number of files deferred after the pass-level budget expired</summary>
    public int BudgetDeferredCount => _files.Count(file => file.Status == SecondOpinionValidationStatus.BudgetDeferred);

    /// <summary>True only when every attempted file produced parseable structured findings</summary>
    public bool SeverityDetermined => _files.All(file => file.Status == SecondOpinionValidationStatus.Validated);

    /// <summary>Total files whose second-opinion validation did not complete</summary>
    public int FailedCount => RequestFailureCount + EmptyResponseCount + ParseFailureCount + BudgetDeferredCount;

    /// <summary>Ordered per-file outcomes accumulated for this validation pass</summary>
    public IReadOnlyList<SecondOpinionFileValidation> Files => _files;

    /// <summary>Records one file's parseable structured validation</summary>
    public void AddValidated(string filePath, IReadOnlyList<LineRange> ranges, ParsedValidation parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        string rangeText = ranges.Count == 0 ? "(entire file)" : string.Join(", ", ranges.Select(range => range.ToString()));
        _files.Add(new SecondOpinionFileValidation(filePath, rangeText, SecondOpinionValidationStatus.Validated, true, parsed.Confirmed, parsed.Discarded, parsed.Verdict, null));

        Severity fileMax = SecondOpinionParser.MaxSeverity(parsed.Confirmed);
        if (fileMax > MaxSeverity)
        {
            MaxSeverity = fileMax;
        }
    }

    /// <summary>Records why one file did not produce parseable structured validation</summary>
    public void AddFailure(string filePath, IReadOnlyList<LineRange> ranges, SecondOpinionValidationStatus status, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!Enum.IsDefined(status) || status == SecondOpinionValidationStatus.Validated)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A failure record must use a failure status");
        }

        string rangeText = ranges.Count == 0 ? "(entire file)" : string.Join(", ", ranges.Select(range => range.ToString()));
        _files.Add(new SecondOpinionFileValidation(filePath, rangeText, status, false, [], [], null, reason));
    }

    /// <summary>Writes the companion json artifact and returns its path</summary>
    public string Write(string directory, string runStamp, SecondOpinionModelSelection selection, string sourceReportPath)
    {
        ArgumentNullException.ThrowIfNull(selection);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"Informant-Report-{runStamp}-validated.json");

        var document = new
        {
            validatingModel = selection.Model.ModelName,
            modelProfile = selection.ProfileName,
            endpoint = selection.Model.Endpoint,
            primaryCandidateSeverity = selection.PrimaryClassification.DisplaySeverity,
            primarySeverityDetermined = selection.PrimaryClassification.SeverityDetermined,
            selectionReason = selection.SelectionReason,
            sourceReport = Path.GetFileName(sourceReportPath),
            maxSeverity = MaxSeverity.ToString(),
            severityDetermined = SeverityDetermined,
            validatedCount = ValidatedCount,
            requestFailureCount = RequestFailureCount,
            emptyResponseCount = EmptyResponseCount,
            parseFailureCount = ParseFailureCount,
            budgetDeferredCount = BudgetDeferredCount,
            fileCount = _files.Count,
            files = _files
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));

        return path;
    }
}
