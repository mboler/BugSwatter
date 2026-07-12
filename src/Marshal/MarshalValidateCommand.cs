using System.Diagnostics;

namespace Marshal;

/// <summary>Deployment pre-flight for Marshal: validates its own config, checks the Informant executable and watch directories, resolves webhook secrets from the environment, and runs "Informant validate" for every job so a broken deployment fails here instead of at 3am</summary>
public static class MarshalValidateCommand
{
    /// <summary>Loads the config, runs every check, prints a checklist and returns 0 when all pass or 1 when any fail</summary>
    public static async Task<int> RunAsync(string configPath)
    {
        // A load failure throws MarshalFatalException, which the caller reports as a fatal validation failure
        var config = MarshalConfig.Load(configPath);

        Console.WriteLine("Marshal configuration validation");
        int failed = 0;

        Report(ref failed, true, "config", "loaded and structurally valid");
        Report(ref failed, File.Exists(config.InformantExecutable), "Informant executable", config.InformantExecutable);

        if (config.Webhook is { Enabled: true } webhook)
        {
            ReportSecret(ref failed, "webhook GitHub secret", webhook.GitHubSecret);
            ReportSecret(ref failed, "webhook Azure DevOps secret", webhook.AzureDevOpsSecret);
        }

        foreach (ReviewJobConfig job in config.Jobs)
        {
            if (job.WatchPath is not null)
            {
                Report(ref failed, Directory.Exists(job.WatchPath), $"job '{job.Name}' watch path", job.WatchPath);
            }

            (int exitCode, string output) = await RunInformantValidateAsync(config.InformantExecutable, job.InformantConfigPath);
            Report(ref failed, exitCode == 0, $"job '{job.Name}' Informant validate", exitCode == 0 ? "passed" : $"exited {exitCode}");

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Console.WriteLine($"      {line.TrimEnd()}");
            }
        }

        Console.WriteLine(failed == 0 ? "Result: PASS" : $"Result: FAIL ({failed} checks failed)");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<(int ExitCode, string Output)> RunInformantValidateAsync(string executable, string jobConfigPath)
    {
        if (!File.Exists(executable))
        {
            return (-1, "Informant executable not found; cannot validate this job");
        }

        var startInfo = new ProcessStartInfo { FileName = executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(jobConfigPath);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, "could not start Informant");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string output = (await stdoutTask).Trim();
        string error = (await stderrTask).Trim();
        return (process.ExitCode, error.Length > 0 ? $"{output}\n{error}" : output);
    }

    private static void ReportSecret(ref int failed, string label, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            Console.WriteLine($"  [INFO] {label}: not configured, that provider will reject webhooks");
            return;
        }

        bool resolved = !string.IsNullOrEmpty(MarshalConfig.ResolveSecret(secret));
        Report(ref failed, resolved, label, resolved ? "resolves to a value" : "environment reference does not resolve");
    }

    private static void Report(ref int failed, bool passed, string label, string detail)
    {
        if (!passed)
        {
            failed++;
        }

        Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {label}: {detail}");
    }
}
