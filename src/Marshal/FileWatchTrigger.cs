using Microsoft.Extensions.Hosting;
using Serilog;

namespace Marshal;

/// <summary>Watches each job's configured directory and enqueues the job once changes settle for the debounce window, so a burst such as a git checkout coalesces into one review</summary>
public sealed class FileWatchTrigger : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ReviewQueue _queue;
    private readonly MarshalConfig _config;

    /// <summary>Creates the trigger over the shared queue</summary>
    public FileWatchTrigger(ReviewQueue queue, MarshalConfig config)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(config);
        
        _queue = queue;
        _config = config;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan quietWindow = TimeSpan.FromSeconds(_config.FileWatchDebounceSeconds);
        var watchers = new List<FileSystemWatcher>();
        var watched = new List<(ReviewJobConfig Job, ChangeDebouncer Debouncer)>();

        try
        {
            foreach (var job in _config.Jobs.Where(job => job.WatchPath is not null))
            {
                var debouncer = new ChangeDebouncer();
                var watcher = new FileSystemWatcher(job.WatchPath!) { IncludeSubdirectories = true, EnableRaisingEvents = true };

                watcher.Changed += (_, _) => debouncer.OnChange(DateTime.UtcNow);
                watcher.Created += (_, _) => debouncer.OnChange(DateTime.UtcNow);
                watcher.Deleted += (_, _) => debouncer.OnChange(DateTime.UtcNow);
                watcher.Renamed += (_, _) => debouncer.OnChange(DateTime.UtcNow);
                watcher.Error += (_, errorArgs) => Log.Warning("File watcher for {Job} reported an error: {Error}", job.Name, DescribeWatcherError(errorArgs.GetException()));

                watchers.Add(watcher);
                watched.Add((job, debouncer));
                Log.Information("Watching {Path} for {Job} with a {Debounce} second quiet window", job.WatchPath, job.Name, _config.FileWatchDebounceSeconds);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                foreach ((ReviewJobConfig job, ChangeDebouncer debouncer) in watched)
                {
                    if (debouncer.ShouldFire(DateTime.UtcNow, quietWindow))
                    {
                        _queue.Enqueue(job, "file changes settled");
                    }
                }
            }
        }
        finally
        {
            foreach (FileSystemWatcher watcher in watchers)
            {
                watcher.Dispose();
            }
        }
    }

    /// <summary>Formats a watcher error for logging; the parameter is explicitly nullable because FileSystemWatcher can raise the Error event without an exception (a non-nullable framework signature hides this), and dereferencing it unguarded inside the event handler would take the process down</summary>
    /// <param name="error">The exception reported by the watcher, or null when the framework provided none</param>
    /// <returns>The exception message, or a placeholder when there was no exception</returns>
    public static string DescribeWatcherError(Exception? error) => error?.Message ?? "(no exception provided)";
}
