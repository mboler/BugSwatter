using System.Net.Sockets;
using BugSwatter.Common;

namespace Informant;

/// <summary>The result of one validation check</summary>
public sealed record ValidationCheck(string Label, bool Passed, string Detail, bool Warning = false);

/// <summary>Runs deployment pre-flight checks so a misconfigured job fails at validate time instead of at 3am: config loads (proven by reaching here), endpoints answer, and every referenced environment variable actually resolves</summary>
public static class ValidateCommand
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Runs every applicable check, prints a checklist to stdout, and returns 0 when all pass or 1 when any fail</summary>
    public static async Task<int> RunAsync(InformantConfig config, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(http);

        IReadOnlyList<ValidationCheck> checks = await GatherChecksAsync(config, http);

        Console.WriteLine("Informant configuration validation");
        foreach (ValidationCheck check in checks)
        {
            string outcome = !check.Passed ? "FAIL" : check.Warning ? "WARN" : "PASS";
            Console.WriteLine($"  [{outcome}] {check.Label}: {check.Detail}");
        }

        int failed = checks.Count(check => !check.Passed);
        Console.WriteLine(failed == 0 ? $"Result: PASS ({checks.Count} checks)" : $"Result: FAIL ({failed} of {checks.Count} checks failed)");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>Runs the checks without printing, for tests and reuse</summary>
    public static async Task<IReadOnlyList<ValidationCheck>> GatherChecksAsync(InformantConfig config, HttpClient http)
    {
        var checks = new List<ValidationCheck>
        {
            new("config", true, "loaded and structurally valid"),
            new("git executable", File.Exists(config.GitExecutablePath), config.GitExecutablePath)
        };

        foreach (PrimaryModelTarget target in config.GetPrimaryModelTargets())
        {
            string label = target.IsFallback ? $"fallback '{target.Name}' model endpoint" : "model endpoint";
            ValidationCheck endpointCheck = await ProbeHttpAsync(http, target.Endpoint, label);
            checks.Add(endpointCheck);
            if (endpointCheck.Passed)
            {
                string capacityLabel = target.IsFallback ? $"fallback '{target.Name}' model context capacity" : "model context capacity";
                checks.Add(await CheckModelCapacityAsync(http, target, config.MaxContextCharacters, capacityLabel));
            }
        }

        if (config.SecondOpinion is { } secondOpinion)
        {
            foreach (NamedSecondOpinionModel configuredModel in secondOpinion.GetConfiguredModels())
            {
                string label = configuredModel.Name == "single" ? "second-opinion" : $"second-opinion profile '{configuredModel.Name}'";
                checks.Add(await ProbeHttpAsync(http, configuredModel.Model.Endpoint, $"{label} endpoint"));
                if (configuredModel.Model.RequiresApiKey)
                {
                    checks.Add(CheckSecretReference($"{label} API key", configuredModel.Model.ApiKey, configuredModel.Model.ResolveApiKey()));
                }
            }
        }

        if (config.Email is { Provider: EmailProvider.Smtp } smtpEmail)
        {
            checks.Add(await ProbeTcpAsync(smtpEmail.SmtpHost, smtpEmail.SmtpPort, "email SMTP host"));
            if (smtpEmail.RequiresAuthentication)
            {
                checks.Add(CheckSecretReference("email SMTP password", smtpEmail.Password, smtpEmail.ResolvePassword()));
            }
        }
        else if (config.Email is { Provider: EmailProvider.AzureCommunicationServices } acsEmail)
        {
            checks.Add(CheckSecretReference("email ACS connection string", acsEmail.AcsConnectionString, acsEmail.ResolveAcsConnectionString()));
        }

        return checks;
    }

    private static async Task<ValidationCheck> ProbeHttpAsync(HttpClient http, string endpoint, string label)
    {
        ModelEndpointProbeResult result = await ModelEndpointProbe.CheckAsync(http, endpoint, ProbeTimeout);
        return result.Reachable
            ? new ValidationCheck($"{label} reachable", true, $"{endpoint} answered {result.StatusCode}")
            : new ValidationCheck($"{label} reachable", false, $"{endpoint} did not answer: {result.Error}");
    }

    private static async Task<ValidationCheck> CheckModelCapacityAsync(HttpClient http, PrimaryModelTarget target, int maxContextCharacters, string label)
    {
        ModelCapacityMetadataResult metadata = await ModelCapacityDiscovery.DiscoverLmStudioAsync(http, target.Endpoint, target.ModelName, ProbeTimeout);
        ModelCapacityAdvisoryResult advisory = ModelCapacityAdvisory.Evaluate(maxContextCharacters, metadata);
        return new ValidationCheck(label, true, advisory.Detail, advisory.Warning);
    }

    private static async Task<ValidationCheck> ProbeTcpAsync(string host, int port, string label)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return new ValidationCheck($"{label} reachable", true, $"{host}:{port} accepted a connection");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return new ValidationCheck($"{label} reachable", false, $"{host}:{port} refused or timed out: {ex.Message}");
        }
    }

    private static ValidationCheck CheckSecretReference(string label, string? reference, string? resolvedValue)
    {
        bool resolved = !string.IsNullOrEmpty(resolvedValue);
        string source = DescribeSecretSource(reference);

        return new ValidationCheck(label, resolved, resolved ? $"{source} is set" : $"{source} is not set");
    }

    private static string DescribeSecretSource(string? reference)
    {
        if (reference is null)
        {
            return "(no reference)";
        }

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return $"environment variable {reference[4..].Trim()}";
        }

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return $"secret file {reference[5..].Trim()}";
        }

        return reference;
    }
}
