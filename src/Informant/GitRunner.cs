using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Result of one git invocation</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs the configured git executable and captures its output. Informant deliberately shells out instead of taking a git library dependency</summary>
public sealed class GitRunner
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);

    private readonly string _gitPath;

    /// <summary>Creates a runner for the given git executable path</summary>
    public GitRunner(string gitPath)
    {
        ArgumentNullException.ThrowIfNull(gitPath);
        _gitPath = gitPath;
    }

    /// <summary>Runs git with the given arguments and returns the exit code and captured output</summary>
    public async Task<GitResult> RunAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitPath, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            // Every command targets its tree explicitly via -C or an absolute path argument; pinning the child working
            // directory to temp means a command that ever forgot to would hit a non-repository and fail instead of
            // acting on whatever directory Informant happened to be launched from
            WorkingDirectory = Path.GetTempPath(),
            Environment =
            {
                // Never let an unattended run hang on a credential or terminal prompt
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "never"
            }
        };

        // Keep non-ASCII paths unescaped so parsing stays trivial
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotepath=false");
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InformantFatalException($"Failed to start git at {_gitPath}");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutSource = new CancellationTokenSource(GitTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // the process exited on its own between the timeout firing and the kill; nothing left to stop
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (Exception)
            {
                // catch-all: output of a killed process is best effort and must not mask the timeout error below
            }

            throw new InformantFatalException($"git {string.Join(' ', arguments)} did not finish within {GitTimeout.TotalMinutes:0} minutes and was killed");
        }

        string standardOutput = await stdoutTask;
        string standardError = await stderrTask;

        Log.Debug("git {Arguments} exited with {ExitCode}", string.Join(' ', arguments), process.ExitCode);

        return new GitResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>Runs git and throws a fatal error when it exits non-zero</summary>
    /// <returns>The captured standard output, unmodified</returns>
    public async Task<string> RunCheckedAsync(params string[] arguments)
    {
        var result = await RunAsync(arguments);

        if (result.ExitCode != 0)
        {
            throw new InformantFatalException($"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {TextSummary.Create(result.StandardError, 2000)}");
        }

        return result.StandardOutput;
    }
}
