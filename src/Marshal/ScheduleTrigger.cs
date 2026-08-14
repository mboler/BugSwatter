using Microsoft.Extensions.Hosting;
using Serilog;

namespace Marshal;

/// <summary>Internal scheduler: fires each job at its configured local times of day and enqueues it. Multiple jobs sharing the same minute all fire</summary>
public sealed class ScheduleTrigger : BackgroundService
{
    private readonly ReviewQueue _queue;
    private readonly MarshalConfig _config;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the trigger over the shared queue and injected clock</summary>
    public ScheduleTrigger(ReviewQueue queue, MarshalConfig config, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _queue = queue;
        _config = config;
        _timeProvider = timeProvider;
    }

    /// <summary>Next moment strictly after <paramref name="now"/> matching the given time of day: today when still ahead, otherwise tomorrow</summary>
    public static DateTime NextOccurrence(DateTime now, TimeOnly timeOfDay)
    {
        DateTime today = now.Date + timeOfDay.ToTimeSpan();
        return today > now ? today : today.AddDays(1);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime now = _timeProvider.GetLocalNow().DateTime;
        var entries = new List<ScheduleEntry>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (ReviewJobConfig job in _config.Jobs)
        {
            foreach (string configuredTime in job.Schedule ?? [])
            {
                TimeOnly time = TimeOnly.Parse(configuredTime);
                string key = $"{ReviewQueue.RepositoryKey(job)}|{time.Ticks}";
                if (!seen.Add(key))
                {
                    Log.Warning("Duplicate daily schedule ignored for {Job} at {Time}", job.Name, time);
                    continue;
                }

                entries.Add(new ScheduleEntry(job, time, NextOccurrence(now, time)));
            }
        }

        if (entries.Count == 0)
        {
            return;
        }

        Log.Information("Schedule trigger started with {Count} daily firing times across {Jobs} jobs", entries.Count, entries.Select(entry => entry.Job.Name).Distinct().Count());

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime nextAt = entries.Min(entry => entry.NextAt);

            try
            {
                await ClockAwareDelay.UntilLocalAsync(_timeProvider, nextAt, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            now = _timeProvider.GetLocalNow().DateTime;

            // Each entry keeps its next calendar occurrence, so a backward clock step cannot schedule the same day twice.
            foreach (ScheduleEntry entry in entries.Where(entry => entry.NextAt <= now))
            {
                DateTime scheduledAt = entry.NextAt;
                _queue.Enqueue(entry.Job, $"schedule {scheduledAt:HH:mm}");
                entry.NextAt = NextOccurrence(now, entry.Time);
            }
        }
    }

    private sealed class ScheduleEntry(ReviewJobConfig job, TimeOnly time, DateTime nextAt)
    {
        public ReviewJobConfig Job { get; } = job;

        public TimeOnly Time { get; } = time;

        public DateTime NextAt { get; set; } = nextAt;
    }
}
