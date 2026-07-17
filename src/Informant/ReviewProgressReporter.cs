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
    private readonly UsageAccumulator _runUsage = new();
    private UsageAccumulator _currentUsage = new();
    private readonly UsageAccumulator _localUsage = new();
    private readonly UsageAccumulator _frontierUsage = new();
    private ModelUsagePricing _currentPricing = new(null, null);

    /// <summary>Creates a reporter over the selected output mode and destination</summary>
    public ReviewProgressReporter(ProgressOutput outputMode, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _enabled = outputMode == ProgressOutput.Json;
        _output = output;
    }

    /// <summary>Reports a phase transition and clears any previous file position</summary>
    public void ReportPhase(string phase, string? modelName = null, string? modelProfile = null, ModelUsagePricing? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            SetScope(phase, modelName, modelProfile, pricing ?? new ModelUsagePricing(null, null));
            _currentFile = null;
            _fileIndex = null;
            _fileCount = null;
            _modelRequestActive = false;
            _modelRequestStartedUtc = null;
            WriteSnapshot();
        }
    }

    /// <summary>Reports the file position in a primary or second-opinion pass</summary>
    public void ReportFile(string phase, string modelName, string modelProfile, string currentFile, int fileIndex, int fileCount, ModelUsagePricing? pricing = null)
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
            SetScope(phase, modelName, modelProfile, pricing ?? new ModelUsagePricing(null, null));
            _currentFile = currentFile;
            _fileIndex = fileIndex;
            _fileCount = fileCount;
            _modelRequestActive = false;
            _modelRequestStartedUtc = null;
            WriteSnapshot();
        }
    }

    /// <summary>Updates the active model without clearing the current phase or file when a primary review fails over between targets</summary>
    public void ReportModelTarget(string modelName, string modelProfile, ModelUsagePricing? pricing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelProfile);
        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            SetScope(_phase, modelName, modelProfile, pricing ?? new ModelUsagePricing(null, null));
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
            SetScope(_phase, progress.ModelName, _modelProfile, _currentPricing);
            UsageAccumulator classifiedUsage = _currentPricing.IsLocal ? _localUsage : _frontierUsage;
            if (progress.State == ModelCallState.Started)
            {
                _modelRequestActive = true;
                _modelRequestStartedUtc = progress.StartedUtc;
                _runUsage.AddRequest();
                _currentUsage.AddRequest();
                classifiedUsage.AddRequest();

                if (!_currentPricing.IsLocal && !_currentPricing.CanEstimate)
                {
                    _runUsage.MarkCostUnavailable();
                    _currentUsage.MarkCostUnavailable();
                    _frontierUsage.MarkCostUnavailable();
                }
            }
            else
            {
                _modelRequestActive = false;
                _modelRequestStartedUtc = null;
                if (progress.State == ModelCallState.Completed)
                {
                    bool estimateCost = !_currentPricing.IsLocal;
                    _runUsage.AddCompleted(progress.Usage, _currentPricing, estimateCost);
                    _currentUsage.AddCompleted(progress.Usage, _currentPricing, estimateCost);
                    classifiedUsage.AddCompleted(progress.Usage, _currentPricing, estimateCost);
                }
                else if (!_currentPricing.IsLocal)
                {
                    _runUsage.MarkCostUnavailable();
                    _currentUsage.MarkCostUnavailable();
                    _frontierUsage.MarkCostUnavailable();
                }
            }

            WriteSnapshot();
        }
    }

    /// <summary>Reports successful completion of the Informant run</summary>
    public void ReportCompleted() => ReportPhase("Completed");

    /// <summary>Reports that the Informant run is ending because of a fatal error</summary>
    public void ReportFailed() => ReportPhase("Failed");

    private void SetScope(string phase, string? modelName, string? modelProfile, ModelUsagePricing pricing)
    {
        if (!string.Equals(_phase, phase, StringComparison.Ordinal) || !string.Equals(_modelName, modelName, StringComparison.Ordinal)
            || !string.Equals(_modelProfile, modelProfile, StringComparison.Ordinal) || _currentPricing != pricing)
        {
            _currentUsage = new UsageAccumulator();
        }

        _phase = phase;
        _modelName = modelName;
        _modelProfile = modelProfile;
        _currentPricing = pricing;
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
                RunUsage = _runUsage.Snapshot(),
                CurrentUsage = _currentUsage.Snapshot(),
                LocalUsage = _localUsage.Snapshot(),
                FrontierUsage = _frontierUsage.Snapshot()
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

    private sealed class UsageAccumulator
    {
        private int _requestCount;
        private long? _promptTokens;
        private long? _completionTokens;
        private long? _totalTokens;
        private decimal _estimatedCost;
        private bool _hasEstimatedCost;
        private bool _costUnavailable;

        public void AddRequest()
        {
            if (_requestCount < int.MaxValue)
            {
                _requestCount++;
            }
        }

        public void AddCompleted(ModelTokenUsage? usage, ModelUsagePricing pricing, bool estimateCost)
        {
            if (usage is not null)
            {
                AddUsage(ref _promptTokens, usage.PromptTokens);
                AddUsage(ref _completionTokens, usage.CompletionTokens);
                AddUsage(ref _totalTokens, usage.TotalTokens);
            }

            if (!estimateCost)
            {
                return;
            }

            decimal? cost = pricing.Estimate(usage);
            if (cost is null)
            {
                _costUnavailable = true;
                return;
            }

            try
            {
                _estimatedCost = checked(_estimatedCost + cost.Value);
                _hasEstimatedCost = true;
            }
            catch (OverflowException)
            {
                _costUnavailable = true;
            }
        }

        public void MarkCostUnavailable() => _costUnavailable = true;

        public ReviewUsageSnapshot Snapshot() => new()
        {
            RequestCount = _requestCount,
            PromptTokens = _promptTokens,
            CompletionTokens = _completionTokens,
            TotalTokens = _totalTokens,
            EstimatedCost = _hasEstimatedCost && !_costUnavailable ? _estimatedCost : null
        };

        private static void AddUsage(ref long? total, long? value)
        {
            if (value is >= 0)
            {
                long current = total ?? 0;
                total = value.Value > long.MaxValue - current ? long.MaxValue : current + value.Value;
            }
        }
    }
}
