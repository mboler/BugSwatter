using System.Diagnostics;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Entry point and run orchestration</summary>
internal static class Program
{
    // One process-lifetime client for all model calls; per-call instances would churn sockets and handlers
    private static readonly HttpClient SharedHttpClient = new();

    private static bool _loggingReady;
    private static bool _consoleLogging;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = CommandLineArguments.Parse(args);

            switch (arguments.Command)
            {
                case "--help" or "-h" or "help":
                    PrintUsage();
                    return 0;

                case "init":
                    return InitCommand.Run(Directory.GetCurrentDirectory());
            }

            (InformantConfig config, string configPath) = LoadConfig(arguments);
            
            _consoleLogging = LoggingSetup.Initialize(config.LogLevel, config.LogFilePath, config.ConsoleLogging);
            _loggingReady = true;
            
            Log.Information("Config loaded from {Path}", configPath);

            switch (arguments.Command)
            {
                case "run":
                    return await RunReviewAsync(config);

                case "verify":
                    return await VerifyToolCallingAsync(config);

                case "validate":
                    return await ValidateCommand.RunAsync(config, SharedHttpClient);

                default:
                    await Console.Error.WriteLineAsync($"Unknown command '{arguments.Command}'");
                    PrintUsage();
                    return 1;
            }
        }
        catch (InformantFatalException ex)
        {
            ReportFatal(ex.Message);
            return 1;
        }
        catch (GitOperationException ex)
        {
            ReportFatal(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            // catch-all so an unattended run always exits with a logged reason instead of an unhandled crash
            ReportFatal(ex.ToString());
            return 1;
        }
        finally
        {
            if (_loggingReady)
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }

    private static (InformantConfig Config, string ConfigPath) LoadConfig(CommandLineArguments arguments)
    {
        string configPath = Path.GetFullPath(arguments.ConfigPath ?? InformantConfig.FileName);
        return (InformantConfig.LoadFile(configPath), configPath);
    }

    private static async Task<int> RunReviewAsync(InformantConfig config)
    {
        var stopwatch = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.Now;
        ApplyReportRetention(config);

        PrintDestructiveTreeWarning(config);
        Log.Information("Informant run starting: repository {Repository}, branch {Branch}, mode {Mode}", config.RepositoryUrl, config.Branch, config.ReviewMode);

        var git = new GitRunner(config.GitExecutablePath);
        var tree = new WorkingTreeManager(git, config.RepositoryUrl, config.Branch, config.WorkingTreePath);
        await tree.EnsureFreshTreeAsync();
        string tipSha = await tree.GetTipShaAsync();

        var state = new ReviewStateStore(config.StateFilePath);
        string? baselineSha = state.GetBaseline(config.RepositoryUrl, config.Branch);
        Log.Information("Tree is at {Tip}; baseline is {Baseline}", tipSha, baselineSha ?? "(none, first run)");

        // One clock read for both the metadata timestamp and the artifact-name stamp keeps them consistent
        string runStamp = startedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        IReadOnlyList<ChangedFile> files = await DetectReviewSetAsync(config, git, baselineSha, tipSha);

        if (files.Count == 0)
        {
            // An empty report is noise: a run with nothing to review leaves no artifacts, only this log record
            state.SetBaseline(config.RepositoryUrl, config.Branch, tipSha);
            Log.Information("Run complete: no changes to review since the baseline; no report written");
            EmitReportPath(null);
            return 0;
        }

        string changesPath = ChangeSetFile.Write(config.ReportDirectory, runStamp, baselineSha, tipSha, config.ReviewMode, files);
        Log.Information("Change set with {Count} files persisted to {Path}", files.Count, changesPath);

        var report = new ReportWriter(config.ReportDirectory, runStamp, config.ModelName, config.ModelEndpoint, config.MaxContextCharacters, config.MaxFileLines);
        report.WriteHeader(config.RepositoryUrl, config.Branch, config.ReviewMode, baselineSha, tipSha, startedAt);

        var client = new ModelClient(SharedHttpClient, config.ModelEndpoint, config.ModelName, TimeSpan.FromSeconds(config.RequestTimeoutSeconds), maxResponseBytes: config.MaxModelResponseBytes);
        await ToolCallingVerifier.RequireToolCallingAsync(client, config.MaxContextCharacters);

        var loop = new ToolCallLoop(client, new ReadFileLinesTool(config.ResolvedAllowedReadRoot, config.MaxFileBytes), config.MaxContextCharacters);
        var reviewer = new FileReviewer(loop, config.WorkingTreePath, config.ResolveReviewPrompt(config.WorkingTreePath), config.MaxFileLines, config.MaxContextCharacters,
            config.PerFileRetryCount, config.MaxFileBytes, git);

        int reviewedCount = 0;
        var skipped = new List<(string Path, string Reason)>();
        var results = new List<FileReviewResult>();
        for (int index = 0; index < files.Count; index++)
        {
            var file = files[index];
            Log.Information("Reviewing {Path} ({Position}/{Count})", file.Path, index + 1, files.Count);

            var result = await reviewer.ReviewAsync(file);
            report.AppendFileSection(result);
            results.Add(result);

            if (result.FullyReviewed)
            {
                reviewedCount++;
            }
            else
            {
                skipped.Add((file.Path, result.SkipReason ?? "unknown"));
            }
        }

        report.Finalize(reviewedCount, skipped, stopwatch.Elapsed);
        bool baselineAdvanced = ReviewCompletion.CanAdvanceBaseline(results);
        if (baselineAdvanced)
        {
            state.SetBaseline(config.RepositoryUrl, config.Branch, tipSha);
            Log.Information("Primary review complete: {Reviewed} reviewed, {Excluded} not reviewable, new baseline {Tip}, report at {Report}", reviewedCount,
                results.Count(result => result.Status == FileReviewStatus.NotReviewable), tipSha, report.ReportPath);
        }
        else
        {
            Log.Warning("Primary review incomplete: {Failed} failed and {Partial} partial; baseline remains {Baseline}. Report at {Report}", results.Count(result => result.Status == FileReviewStatus.Failed),
                results.Count(result => result.Status == FileReviewStatus.Partial), baselineSha ?? "(none)", report.ReportPath);
        }

        // The second pass is strictly additive: the local review, its report and the baseline are already settled above
        string primaryReportPath = report.ReportPath;
        if (config.SecondOpinion is { } secondOpinion)
        {
            SecondOpinionOutcome? outcome = await RunSecondOpinionAsync(secondOpinion, config, git, results, runStamp, report.ReportPath);

            if (outcome is not null)
            {
                // The validated report supersedes the local one as the run's primary artifact
                primaryReportPath = outcome.ValidatedReportPath;

                // Email is gated on a completed second opinion, so it only fires when there is a validated report to send
                if (config.Email is { } email)
                {
                    await SendReportEmailAsync(email, config, outcome, report.ReportPath);
                }
            }
        }

        EmitReportPath(primaryReportPath);
        return 0;
    }

    private static void ApplyReportRetention(InformantConfig config)
    {
        try
        {
            var retention = new ReportRetentionService(config.ReportDirectory, config.ReportRetentionDays, TimeProvider.System);
            ReportRetentionResult result = retention.DeleteExpired();
            if (result.DeletedCount > 0)
            {
                Log.Information("Report retention deleted {Count} artifacts older than {Days} days from {Directory}", result.DeletedCount, config.ReportRetentionDays, config.ReportDirectory);
            }
        }
        catch (Exception ex)
        {
            // catch-all: retention is housekeeping and must never prevent the configured review from running.
            Log.Warning("Report retention could not run for {Directory}: {Reason}; continuing the review", config.ReportDirectory, ex.Message);
        }
    }

    /// <summary>Prints the run's primary report path, or "none" when no report was produced, on its own stdout line so a parent supervisor such as Marshal records the exact artifact instead of guessing the newest file by timestamp. Emitted only when output is redirected, so an interactive run's console stays clean and relies on the logged path instead</summary>
    private static void EmitReportPath(string? reportPath)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine($"{ReportMarker.Prefix} {(reportPath is null ? "none" : Path.GetFullPath(reportPath))}");
        }
    }

    private static async Task SendReportEmailAsync(EmailConfig email, InformantConfig config, SecondOpinionOutcome outcome, string localReportPath)
    {
        string provider = email.Provider.ToString();
        try
        {
            // Fail open: when the second opinion produced no parseable severity, send anyway rather than risk dropping a real finding
            bool severityUndetermined = !outcome.SeverityDetermined;
            if (!email.ShouldSend(outcome.MaxSeverity, outcome.SeverityDetermined))
            {
                Log.Information("Email skipped: max confirmed severity {Severity} is below the '{Threshold}' send threshold", outcome.MaxSeverity, email.SendOn);
                RecordEmailDelivery(outcome.ValidatedReportPath, new EmailDeliveryRecord("Skipped", DateTimeOffset.Now, provider, email.To, $"max confirmed severity {outcome.MaxSeverity} is below the '{email.SendOn}' send threshold"));
                return;
            }

            IEmailSender? sender = BuildEmailSender(email);
            if (sender is null)
            {
                RecordEmailDelivery(outcome.ValidatedReportPath, new EmailDeliveryRecord("Skipped", DateTimeOffset.Now, provider, email.To, "a required secret is unset or the provider is unsupported (see the log)"));
                return;
            }

            EmailMessage message = EmailReportBuilder.Build(email.From, email.To, config.RepositoryUrl, config.Branch, localReportPath, outcome, severityUndetermined, email.AttachReports);
            EmailSendReceipt receipt = await sender.SendAsync(message);
            string messageId = string.IsNullOrWhiteSpace(receipt.MessageId) ? "" : $"; operation/message ID: {receipt.MessageId}";

            Log.Information("Report email accepted for delivery to {Recipients} (subject: {Subject}, operation/message ID: {MessageId})", string.Join(", ", email.To), message.Subject,
                receipt.MessageId ?? "not provided");
            RecordEmailDelivery(outcome.ValidatedReportPath, new EmailDeliveryRecord(receipt.Decision, DateTimeOffset.Now, provider, email.To, $"subject: {message.Subject}; {receipt.Detail}{messageId}"));
        }
        catch (Exception ex)
        {
            // catch-all: email is a courtesy on top of a finished run; a relay failure must never fail the run or lose the reports
            Log.Error(ex, "Failed to send the report email. The reports stand on disk");
            RecordEmailDelivery(outcome.ValidatedReportPath, new EmailDeliveryRecord("Failed", DateTimeOffset.Now, provider, email.To, ex.Message));
        }
    }

    private static void RecordEmailDelivery(string validatedReportPath, EmailDeliveryRecord record)
    {
        try
        {
            File.AppendAllText(validatedReportPath, record.ToMarkdownSection());
        }
        catch (Exception ex)
        {
            // catch-all: recording the email outcome is a courtesy on a finished run; never fail over it
            Log.Warning("Could not record the email-delivery outcome in {Path}: {Reason}", validatedReportPath, ex.Message);
        }
    }

    private static IEmailSender? BuildEmailSender(EmailConfig email)
    {
        switch (email.Provider)
        {
            case EmailProvider.Smtp:
                string? password = email.ResolvePassword();
                if (email.RequiresAuthentication && string.IsNullOrEmpty(password))
                {
                    Log.Error("Email SMTP is configured with a username but the password environment variable '{Reference}' is not set; skipping the email. The reports stand on disk", email.Password);
                    return null;
                }

                return new SmtpEmailSender(new SmtpEmailOptions(email.SmtpHost, email.SmtpPort, email.UseStartTls, email.Username, password));

            case EmailProvider.AzureCommunicationServices:
                string? connectionString = email.ResolveAcsConnectionString();
                if (string.IsNullOrEmpty(connectionString))
                {
                    Log.Error("Email ACS is configured but the connection-string environment variable '{Reference}' is not set; skipping the email. The reports stand on disk", email.AcsConnectionString);
                    return null;
                }

                return new AcsEmailSender(connectionString);

            default:
                Log.Error("Email provider {Provider} is not supported; skipping the email", email.Provider);
                return null;
        }
    }

    private static async Task<SecondOpinionOutcome?> RunSecondOpinionAsync(SecondOpinionConfig secondOpinion, InformantConfig config, GitRunner git, IReadOnlyList<FileReviewResult> results, string runStamp,
        string sourceReportPath)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string? apiKey = secondOpinion.ResolveApiKey();
            if (secondOpinion.RequiresApiKey && string.IsNullOrEmpty(apiKey))
            {
                Log.Error("Second opinion is configured but the environment variable referenced by '{Reference}' is not set; skipping the validation pass. The local review report stands", secondOpinion.ApiKey);
                return null;
            }

            var client = new ModelClient(SharedHttpClient, secondOpinion.Endpoint, secondOpinion.ModelName, TimeSpan.FromSeconds(secondOpinion.RequestTimeoutSeconds), apiKey, config.MaxModelResponseBytes);

            // Gate: prove endpoint, model and key with a minimal round trip before any code leaves the machine
            await client.CompleteAsync([new ChatMessage { Role = "user", Content = "Reply with the single word READY." }], []);
            Log.Information("Second-opinion endpoint verified: {Model} at {Endpoint}", secondOpinion.ModelName, secondOpinion.Endpoint);

            List<FileReviewResult> toValidate = [.. results.Where(result => result.Findings is not null)];

            // Optionally also look at files the local reviewer could not review, so a changed file the first model skipped is not left silently unreviewed
            if (secondOpinion.ReviewSkippedFiles)
            {
                toValidate.AddRange(results.Where(result => result.Findings is null && result.SkipReason is not null));
            }

            if (toValidate.Count == 0)
            {
                Log.Information("Second opinion: nothing to validate");
                return null;
            }

            // The local reviewer must support tool-calling, but the validator need not: probe it, and when it does,
            // let it read more of a file on demand within a per-file budget; when it does not, it validates from the excerpt only
            VerificationResult toolProbe = await ToolCallingVerifier.VerifyAsync(client, config.MaxContextCharacters);
            Log.Information("Second-opinion tool-calling: {Status} ({Detail})", toolProbe.Success ? $"supported, up to {secondOpinion.MaxFileReads} reads per file" : "not supported, validating from the excerpt only", toolProbe.Detail);

            var validator = new SecondOpinionReviewer(client, config.WorkingTreePath, secondOpinion.ResolvePrompt(), config.MaxContextCharacters, secondOpinion.ContextLines, toolProbe.Success, secondOpinion.MaxFileReads,
                config.MaxFileBytes, git);
            var writer = new SecondOpinionReportWriter(config.ReportDirectory, runStamp);
            writer.WriteHeader(secondOpinion.ModelName, secondOpinion.Endpoint, sourceReportPath, DateTimeOffset.Now, secondOpinion.ContextLines);
            var jsonReport = new SecondOpinionJsonReport();

            int validated = 0;
            int failed = 0;
            for (int index = 0; index < toValidate.Count; index++)
            {
                var result = toValidate[index];
                Log.Information("Second opinion for {Path} ({Position}/{Count})", result.File.Path, index + 1, toValidate.Count);

                string? validation = await validator.ValidateAsync(result);
                if (validation is null)
                {
                    failed++;
                    writer.AppendFileSection(result.File.Path, result.File.ChangedRanges, null);
                    jsonReport.Add(result.File.Path, result.File.ChangedRanges, null);
                    continue;
                }

                validated++;

                // Pull the structured verdict for the companion json and the email gate; on failure the prose is kept intact
                bool parseOk = SecondOpinionParser.TryParse(validation, out ParsedValidation? parsed, out string prose);
                writer.AppendFileSection(result.File.Path, result.File.ChangedRanges, prose);
                jsonReport.Add(result.File.Path, result.File.ChangedRanges, parseOk ? parsed : null);
            }

            writer.Finalize(validated, failed, stopwatch.Elapsed);
            string jsonPath = jsonReport.Write(config.ReportDirectory, runStamp, secondOpinion.ModelName, secondOpinion.Endpoint, sourceReportPath);
            Log.Information("Second opinion complete: {Validated} validated, {Failed} failed, max confirmed severity {Severity}, reports at {Report} and {Json}", validated, failed, jsonReport.MaxSeverity, writer.ReportPath, jsonPath);

            return new SecondOpinionOutcome(writer.ReportPath, jsonPath, jsonReport.MaxSeverity, jsonReport.SeverityDetermined, validated, failed);
        }
        catch (ModelCallException ex)
        {
            Log.Error("Second-opinion pass failed: {Reason}. The local review report stands", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            // catch-all: the second pass is optional and additive; no failure in it may disturb the completed local run
            Log.Error(ex, "Second-opinion pass failed unexpectedly. The local review report stands");
            return null;
        }
    }

    private static async Task<int> VerifyToolCallingAsync(InformantConfig config)
    {
        var client = new ModelClient(SharedHttpClient, config.ModelEndpoint, config.ModelName, TimeSpan.FromSeconds(config.RequestTimeoutSeconds), maxResponseBytes: config.MaxModelResponseBytes);

        var result = await ToolCallingVerifier.VerifyAsync(client, config.MaxContextCharacters);
        Log.Information("Tool-calling verification against {Endpoint} with model {Model}: {Outcome}. {Detail}", config.ModelEndpoint, config.ModelName, result.Success ? "PASSED" : "FAILED", result.Detail);

        // The Serilog console sink already surfaces the verdict when it is active; when it is off (redirected output
        // or a config override) write the verdict to stdout directly so the invoker always receives exactly one copy
        if (!_consoleLogging)
        {
            Console.WriteLine($"Tool-calling verification {(result.Success ? "PASSED" : "FAILED")}: {result.Detail}");
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<IReadOnlyList<ChangedFile>> DetectReviewSetAsync(InformantConfig config, GitRunner git, string? baselineSha, string tipSha)
    {
        var detector = new ChangeDetector(git, config.WorkingTreePath);

        if (config.ReviewMode == ReviewMode.Full)
        {
            return await detector.GetAllFilesAsync();
        }

        if (baselineSha is null)
        {
            Log.Information("No baseline recorded for this repository and branch; the first run reviews the entire tree");
            return await detector.GetAllFilesAsync();
        }

        if (baselineSha == tipSha)
        {
            Log.Information("Tip equals the baseline; there are no changes to review");
            return [];
        }

        if (!await detector.IsCommitReachableAsync(baselineSha))
        {
            Log.Warning("Baseline {Baseline} no longer exists in the repository (history was likely rewritten); falling back to a full review", baselineSha);
            return await detector.GetAllFilesAsync();
        }

        return await detector.GetChangedFilesAsync(baselineSha, tipSha);
    }

    private static void PrintDestructiveTreeWarning(InformantConfig config)
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=================================================================");
        Console.WriteLine(" WARNING: Informant owns the working tree at");
        Console.WriteLine($"   {config.WorkingTreePath}");
        Console.WriteLine(" It is OVERWRITTEN DESTRUCTIVELY (hard reset + clean) on every");
        Console.WriteLine(" run. Do not keep any work of your own in that directory.");
        Console.WriteLine("=================================================================");
        Console.WriteLine();
    }

    private static void ReportFatal(string message)
    {
        if (_loggingReady)
        {
            Log.Fatal("Run aborted: {Reason}", message);
        }

        if (!_loggingReady || !_consoleLogging)
        {
            Console.Error.WriteLine($"Informant fatal: {message}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Informant, an unattended local-model code-review harness");
        Console.WriteLine();
        Console.WriteLine("Usage (run from the directory holding informant.json, or pass --config):");
        Console.WriteLine("  Informant [--config <path>]           run a review");
        Console.WriteLine("  Informant init                        write a starter informant.json and review-prompt.txt");
        Console.WriteLine("  Informant verify [--config <path>]    prove the configured model performs tool-calling, then exit");
        Console.WriteLine("  Informant validate [--config <path>]  check config, endpoint reachability and secrets, then exit");
        Console.WriteLine("  Informant help                        show this help");
        Console.WriteLine();
        Console.WriteLine("--config names the config file explicitly; relative paths inside the config resolve against that file's directory. Without it, informant.json is read from the current directory");
    }
}
