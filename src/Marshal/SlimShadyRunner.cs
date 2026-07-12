using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using BugSwatter.Common;
using Serilog;

namespace Marshal;

/// <summary>Result of one supervised child process</summary>
public sealed record ProcessRunResult(int? ExitCode, bool TimedOut, string StandardOutput);

/// <summary>Outcome of one dispatched SlimShady run</summary>
public sealed record ReviewRunOutcome(int? ExitCode, TimeSpan Duration, bool TimedOut, string? ReportPath, string? Error)
{
    /// <summary>True when the child ran to completion and reported success</summary>
    public bool Succeeded => !TimedOut && Error is null && ExitCode == 0;
}

/// <summary>Launches and supervises one review; stubbed in tests so no real review runs</summary>
public interface ISlimShadyRunner
{
    /// <summary>Runs SlimShady for the job and reports the outcome; never throws for child failures</summary>
    Task<ReviewRunOutcome> RunAsync(ReviewJobConfig job, CancellationToken cancellationToken);
}

/// <summary>Runs SlimShady as a child process with the job's config, enforcing the per-run timeout by killing the entire process tree</summary>
public sealed class SlimShadyProcessRunner : ISlimShadyRunner
{
    private readonly MarshalConfig _config;

    /// <summary>Creates a runner bound to the Marshal config</summary>
    public SlimShadyProcessRunner(MarshalConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <inheritdoc />
    public async Task<ReviewRunOutcome> RunAsync(ReviewJobConfig job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var stopwatch = Stopwatch.StartNew();
        DateTime startedUtc = DateTime.UtcNow;

        try
        {
            ProcessRunResult result = await RunProcessAsync(_config.SlimShadyExecutable, ["--config", job.SlimShadyConfigPath], TimeSpan.FromMinutes(_config.PerRunTimeoutMinutes), cancellationToken);
            string? reportPath = result.TimedOut ? null : ResolveReportPath(result.StandardOutput, job.SlimShadyConfigPath, startedUtc);
            
            return new ReviewRunOutcome(result.ExitCode, stopwatch.Elapsed, result.TimedOut, reportPath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // catch-all: supervision must survive any child launch failure and report it as a failed run, never kill the dispatcher
            Log.Error(ex, "Failed to launch SlimShady for job {Job}", job.Name);
            return new ReviewRunOutcome(null, stopwatch.Elapsed, false, null, ex.Message);
        }
    }

    /// <summary>Starts a process and waits for exit; when <paramref name="timeout"/> elapses first, the entire process tree is killed and the result is marked timed out</summary>
    /// <returns>The exit code on completion, or a null exit code with TimedOut set when the tree was killed</returns>
    public static async Task<ProcessRunResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new MarshalFatalException($"Failed to start process {fileName}");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // the child exited on its own between the timeout firing and the kill; nothing left to stop
            }

            await ObserveOutputAsync(stdoutTask, stderrTask);
            return new ProcessRunResult(null, true, string.Empty);
        }

        string standardError = (await stderrTask).Trim();
        string standardOutput = await stdoutTask;
        if (process.ExitCode != 0 && standardError.Length > 0)
        {
            Log.Warning("Child {FileName} exited {ExitCode} with stderr: {StdErr}", fileName, process.ExitCode, standardError.Length <= 1000 ? standardError : standardError[..1000] + " [truncated]");
        }

        return new ProcessRunResult(process.ExitCode, false, standardOutput);
    }

    private static async Task ObserveOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (Exception)
        {
            // catch-all: output of a killed process is best effort and must not mask the timeout outcome
        }
    }

    /// <summary>Best-effort discovery of the report a run produced: reads the job's SlimShady config (which may carry comments, like SlimShady's own loader allows) for its report directory and returns the newest report written since the run started, or null</summary>
    public static string? DiscoverReportPath(string slimShadyConfigPath, DateTime startedUtc)
    {
        try
        {
            string configDirectory = Path.GetDirectoryName(Path.GetFullPath(slimShadyConfigPath)) ?? ".";
            var documentOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var document = JsonDocument.Parse(File.ReadAllText(slimShadyConfigPath), documentOptions);
            string reportDirectory = document.RootElement.TryGetProperty("reportDirectory", out JsonElement element) ? element.GetString() ?? "reports" : "reports";
            string fullReportDirectory = Path.IsPathFullyQualified(reportDirectory) ? reportDirectory : Path.Combine(configDirectory, reportDirectory);

            return Directory.EnumerateFiles(fullReportDirectory, "SlimShady-Report-*.md")
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc >= startedUtc.AddMinutes(-1))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception)
        {
            // catch-all: report discovery is best effort; an unreadable config or missing directory just means no path is logged
            return null;
        }
    }

    /// <summary>Resolves the run's report path: prefers the absolute path SlimShady printed on stdout, which is authoritative and distinguishes a produced report from a no-change run, and falls back to timestamp-based discovery only when no marker was printed at all (an older SlimShady, or a crash before the marker)</summary>
    public static string? ResolveReportPath(string standardOutput, string slimShadyConfigPath, DateTime startedUtc)
    {
        (bool found, string? path) = ParseReportMarker(standardOutput);

        return found ? path : DiscoverReportPath(slimShadyConfigPath, startedUtc);
    }

    /// <summary>Parses the SLIMSHADY-REPORT stdout marker. Found is true with the path when SlimShady named a report, true with a null path when it explicitly reported none, and false when no marker was printed at all</summary>
    public static (bool Found, string? Path) ParseReportMarker(string standardOutput)
    {
        (bool Found, string? Path) marker = (false, null);
        foreach (string line in standardOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(ReportMarker.Prefix, StringComparison.Ordinal))
            {
                string value = trimmed[ReportMarker.Prefix.Length..].Trim();
                marker = (true, value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value);
            }
        }

        return marker;
    }
}
