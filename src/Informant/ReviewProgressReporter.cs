using BugSwatter.AI;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Emits complete, versioned review snapshots only when explicitly requested, while keeping normal standalone output unchanged by default</summary>
public sealed class ReviewProgressReporter
{
    private readonly Lock _gate = new();
    private readonly TextWriter _output;
    private readonly bool _enabled;

    private string _phase = "Starting";
    private string? _modelName;
    private string? _modelProfile;
    private string? _currentFile;
    private int? _fileIndex;
    private int? _fileCount;
    private bool _modelRequestActive;
    private DateTimeOffset? _modelRequestStartedUtc;
    private int _modelRequestCount;
    private long? _promptTokens;
    private long? _completionTokens;
    private long? _totalTokens;

    /// <summary>Creates a reporter over the selected output mode and destination</summary>
    public ReviewProgressReporter(ProgressOutput outputMode, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _enabled = outputMode == ProgressOutput.Json;
        _output = output;
    }

    /// <summary>Reports a phase transition and clears any previous file position</summary>
    public void ReportPhase(string phase, string? modelName = null, string? modelProfile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            _phase = phase;
            _modelName = modelName;
            _modelProfile = modelProfile;
            _currentFile = null;
            _fileIndex = null;
            _fileCount = null;
            _modelRequestActive = false;
            _modelRequestStartedUtc = null;
            WriteSnapshot();
        }
    }

    /// <summary>Reports the file position in a primary or second-opinion pass</summary>
    public void ReportFile(string phase, string modelName, string modelProfile, string currentFile, int fileIndex, int fileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileCount);
        if (fileIndex > fileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex), fileIndex, "The file position cannot exceed the file count");
        }

        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            _phase = phase;
            _modelName = modelName;
            _modelProfile = modelProfile;
            _currentFile = currentFile;
            _fileIndex = fileIndex;
            _fileCount = fileCount;
            _modelRequestActive = false;
            _modelRequestStartedUtc = null;
            WriteSnapshot();
        }
    }

    /// <summary>Observes model request lifecycle events, request counts, and provider-reported token usage</summary>
    public void ObserveModelCall(ModelCallProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            _modelName = progress.ModelName;
            if (progress.State == ModelCallState.Started)
            {
                _modelRequestActive = true;
                _modelRequestStartedUtc = progress.StartedUtc;
                _modelRequestCount++;
            }
            else
            {
                _modelRequestActive = false;
                _modelRequestStartedUtc = null;
                if (progress.State == ModelCallState.Completed && progress.Usage is not null)
                {
                    AddUsage(ref _promptTokens, progress.Usage.PromptTokens);
                    AddUsage(ref _completionTokens, progress.Usage.CompletionTokens);
                    AddUsage(ref _totalTokens, progress.Usage.TotalTokens);
                }
            }

            WriteSnapshot();
        }
    }

    /// <summary>Reports successful completion of the Informant run</summary>
    public void ReportCompleted() => ReportPhase("Completed");

    /// <summary>Reports that the Informant run is ending because of a fatal error</summary>
    public void ReportFailed() => ReportPhase("Failed");

    private static void AddUsage(ref long? total, long? value)
    {
        if (value is >= 0)
        {
            long current = total ?? 0;
            total = value.Value > long.MaxValue - current ? long.MaxValue : current + value.Value;
        }
    }

    private void WriteSnapshot()
    {
        try
        {
            var snapshot = new ReviewProgressSnapshot
            {
                Phase = _phase,
                ModelName = _modelName,
                ModelProfile = _modelProfile,
                CurrentFile = _currentFile,
                FileIndex = _fileIndex,
                FileCount = _fileCount,
                ModelRequestActive = _modelRequestActive,
                ModelRequestStartedUtc = _modelRequestStartedUtc,
                ModelRequestCount = _modelRequestCount,
                PromptTokens = _promptTokens,
                CompletionTokens = _completionTokens,
                TotalTokens = _totalTokens
            };
            _output.WriteLine(ReviewProgressMarker.Format(snapshot));
            _output.Flush();
        }
        catch (Exception ex)
        {
            // catch-all: progress is optional telemetry and must never alter the review outcome
            Log.Warning("Could not write review progress: {Reason}", ex.Message);
        }
    }
}
