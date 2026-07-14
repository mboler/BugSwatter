using System.Diagnostics;
using System.Text.Json;

namespace Informant.Tests;

/// <summary>Exercises manifest behavior through the Informant executable boundary</summary>
[Collection("Informant configuration environment")]
public sealed class ProgramManifestIntegrationTests
{
    /// <summary>Verifies no-change runs rebuild inventory without writing artifacts or calling a model</summary>
    [Fact]
    public async Task NoChangeRunRebuildsManifestWithoutPersistingArtifactsOrCallingAModel()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        string tip = await repository.CommitFileAsync("source.cs", "class Source { }", "initial");
        string workingTreePath = Path.Combine(repository.Root, "managed-tree");
        string reportDirectory = Path.Combine(repository.Root, "reports");
        string statePath = Path.Combine(repository.Root, "informant.state.json");
        string configPath = Path.Combine(repository.Root, "informant.json");
        new ReviewStateStore(statePath).SetBaseline(repository.RemotePath, "main", tip);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            repositoryUrl = repository.RemotePath,
            branch = "main",
            workingTreePath,
            gitExecutablePath = TestGit.ExecutablePath,
            modelEndpoint = "http://127.0.0.1:1/v1",
            modelName = "must-not-be-called",
            reportDirectory,
            stateFilePath = statePath,
            reviewPrompt = "unused",
            promptIncludeFiles = Array.Empty<string>(),
            logFilePath = Path.Combine(repository.Root, "logs", "informant-.log"),
            consoleLogging = true
        }));

        string informantAssembly = typeof(InformantConfig).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(informantAssembly);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        foreach (string key in startInfo.Environment.Keys.Where(key => key.StartsWith("INFORMANT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(key);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Informant integration-test process");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await process.WaitForExitAsync(timeout.Token);
        string standardOutput = await standardOutputTask;
        string standardError = await standardErrorTask;

        Assert.True(process.ExitCode == 0, $"Informant exited with {process.ExitCode}{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
        Assert.Contains("Repository manifest rebuilt", standardOutput, StringComparison.Ordinal);
        Assert.Contains("no changes to review", standardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(reportDirectory));
    }
}
