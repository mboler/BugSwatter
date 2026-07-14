using System.Globalization;
using Serilog;

namespace Informant;

/// <summary>Outcome of one report-retention cleanup</summary>
public sealed record ReportRetentionResult(int DeletedCount, int FailedCount);

/// <summary>Deletes only expired, top-level artifacts whose names exactly match files Informant produces</summary>
public sealed class ReportRetentionService
{
    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";
    private const int TimestampLength = 19;
    private const string ReportPrefix = "Informant-Report-";
    private const string ChangesPrefix = "Informant-Changes-";
    private const string ManifestPrefix = "Informant-Manifest-";

    private readonly string _reportDirectory;
    private readonly int _retentionDays;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a retention service for one report directory</summary>
    public ReportRetentionService(string reportDirectory, int retentionDays, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (retentionDays != -1 && retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), retentionDays, "Retention must be -1 or at least one day");
        }

        _reportDirectory = Path.GetFullPath(reportDirectory);
        _retentionDays = retentionDays;
        _timeProvider = timeProvider;
    }

    /// <summary>Deletes expired managed artifacts and returns deletion and failure counts; -1 and a missing directory are no-ops</summary>
    public ReportRetentionResult DeleteExpired()
    {
        if (_retentionDays == -1)
        {
            return new ReportRetentionResult(0, 0);
        }

        var directory = new DirectoryInfo(_reportDirectory);
        if (!directory.Exists)
        {
            return new ReportRetentionResult(0, 0);
        }

        try
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Log.Warning("Report retention refused reparse-point directory {Directory}", _reportDirectory);
                return new ReportRetentionResult(0, 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Report retention could not inspect directory {Directory}: {Reason}", _reportDirectory, ex.Message);
            return new ReportRetentionResult(0, 1);
        }

        DateTime nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        double availableHistoryDays = (nowUtc - DateTime.MinValue).TotalDays;
        DateTime cutoffUtc = _retentionDays >= availableHistoryDays ? DateTime.MinValue : nowUtc.AddDays(-_retentionDays);
        int deletedCount = 0;
        int failedCount = 0;
        try
        {
            foreach (FileInfo file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (!IsManagedArtifact(file.Name))
                {
                    continue;
                }

                try
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Log.Warning("Report retention refused reparse-point artifact {Path}", file.FullName);
                        failedCount++;
                        continue;
                    }

                    if (file.LastWriteTimeUtc >= cutoffUtc)
                    {
                        continue;
                    }

                    file.Delete();
                    deletedCount++;
                }
                catch (FileNotFoundException)
                {
                    // Another cleanup or operator already removed the file, which satisfies retention
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warning("Report retention could not delete {Path}: {Reason}", file.FullName, ex.Message);
                    failedCount++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Report retention could not enumerate {Directory}: {Reason}", _reportDirectory, ex.Message);
            failedCount++;
        }

        return new ReportRetentionResult(deletedCount, failedCount);
    }

    private static bool IsManagedArtifact(string fileName)
    {
        if (fileName.StartsWith(ReportPrefix, StringComparison.Ordinal))
        {
            return HasValidTimestampAndSuffix(fileName, ReportPrefix, [".md", "-validated.md", "-validated.json"]);
        }

        if (fileName.StartsWith(ChangesPrefix, StringComparison.Ordinal))
        {
            return HasValidTimestampAndSuffix(fileName, ChangesPrefix, [".json"]);
        }

        return fileName.StartsWith(ManifestPrefix, StringComparison.Ordinal)
            && HasValidTimestampAndSuffix(fileName, ManifestPrefix, [".json"]);
    }

    private static bool HasValidTimestampAndSuffix(string fileName, string prefix, IReadOnlyList<string> allowedSuffixes)
    {
        if (fileName.Length <= prefix.Length + TimestampLength)
        {
            return false;
        }

        string timestamp = fileName.Substring(prefix.Length, TimestampLength);
        string suffix = fileName[(prefix.Length + TimestampLength)..];
        return allowedSuffixes.Contains(suffix, StringComparer.Ordinal)
            && DateTime.TryParseExact(timestamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
