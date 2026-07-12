using Microsoft.Extensions.Hosting;
using Serilog;

namespace Marshal;

/// <summary>Internal scheduler: fires each job at its configured local times of day and enqueues it. Multiple jobs sharing the same minute all fire</summary>
public sealed class ScheduleTrigger : BackgroundService
{
    private readonly ReviewQueue _queue;
    private readonly MarshalConfig _config;

    /// <summary>Creates the trigger over the shared queue</summary>
    public ScheduleTrigger(ReviewQueue queue, MarshalConfig config)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        _queue = queue;
        _config = config;
    }

    /// <summary>Next moment at or after <paramref name="now"/> matching the given time of day: today when still ahead, otherwise tomorrow</summary>
    public static DateTime NextOccurrence(DateTime now, TimeOnly timeOfDay)
    {
        DateTime today = now.Date + timeOfDay.ToTimeSpan();
        return today > now ? today : today.AddDays(1);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var entries = new List<(ReviewJobConfig Job, TimeOnly Time)>();
        foreach (ReviewJobConfig job in _config.Jobs)
        {
            entries.AddRange(from time in job.Schedule ?? [] select (job, TimeOnly.Parse(time)));
        }

        if (entries.Count == 0)
        {
            return;
        }

        Log.Information("Schedule trigger started with {Count} daily firing times across {Jobs} jobs", entries.Count, entries.Select(entry => entry.Job.Name).Distinct().Count());

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime now = DateTime.Now;

            var upcoming = entries.Select(entry => (entry.Job, At: NextOccurrence(now, entry.Time))).OrderBy(candidate => candidate.At).ToList();
            DateTime nextAt = upcoming[0].At;

            try
            {
                await Task.Delay(nextAt - now, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Fire every entry due at this moment, so jobs sharing a time are not skipped past
            foreach ((ReviewJobConfig job, DateTime at) in upcoming.Where(candidate => (candidate.At - nextAt).Duration() < TimeSpan.FromSeconds(1)))
            {
                _queue.Enqueue(job, $"schedule {at:HH:mm}");
            }
        }
    }
}
