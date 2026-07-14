namespace Informant;

/// <summary>Writes the starter config and default review prompt into the current working directory</summary>
public static class InitCommand
{
    /// <summary>Name of the prompt file the starter config points at</summary>
    public const string PromptFileName = "review-prompt.txt";

    // The JSON configuration provider accepts comments and trailing commas, so the starter config keeps its guidance inline; DOCUMENTATION.md has the full reference
    private const string ConfigTemplate = """
        {
          // Every field is documented in DOCUMENTATION.md. Any key can be overridden by a prefixed environment
          // variable, for example INFORMANT_ModelName or the nested INFORMANT_SecondOpinion__ModelName.

          // Git remote to clone and review
          "repositoryUrl": "https://github.com/your-org/your-repo.git",
          "branch": "develop",

          // Absolute path of the working tree Informant owns; it is OVERWRITTEN DESTRUCTIVELY on every run.
          // Never point this at a checkout you work in.
          "workingTreePath": "C:\\BugSwatter\\trees\\your-repo",

          // Full path of the git executable (Linux example: /usr/bin/git)
          "gitExecutablePath": "C:\\Program Files\\Git\\cmd\\git.exe",

          // Directory the read_file_lines tool may read from; null means the working tree
          "allowedReadRoot": null,

          // OpenAI-compatible endpoint base URL (LM Studio, LiteLLM gateway, llama-server, Ollama, ...)
          "modelEndpoint": "http://localhost:1234/v1",
          "modelName": "your-model-name",

          // Optional ordered alternatives that must already be loaded and serving requests; Informant never loads models.
          // After retries are exhausted, the failed file is restarted on the next verified target and the run keeps one report.
          // Example: [{ "name": "backup-server", "endpoint": "http://backup-host:1234/v1", "modelName": "backup-model" }]
          "fallbackModels": [],

          // "changed" reviews only files changed since the last reviewed SHA; "full" reviews the whole tree
          "reviewMode": "changed",

          // "exhaustive" deeply reviews every candidate; "adaptive" may defer full-file review but still covers changed content
          "reviewStrategy": "exhaustive",

          "reportDirectory": "reports",

          // Delete recognized report artifacts after this many days; -1 keeps them forever
          "reportRetentionDays": 31,
          "stateFilePath": "informant.state.json",

          // Inline prompt text wins over the prompt file; when both are null the built-in default is used
          "reviewPrompt": null,
          "reviewPromptFile": "review-prompt.txt",

          // Markdown guidance files appended to the prompt: globs match at the working-tree root, absolute paths name
          // exact files. Defaults to none; this starter opts in with AGENTS.md, and an empty list disables it.
          "promptIncludeFiles": ["AGENTS.md"],

          // Optional repository-relative files, directories, or globs to prioritize as planning context.
          // Seeds do not bypass manifest safety checks or the configured context budget.
          "seedPaths": [],

          // Target character budget per review call, kept deliberately below the model's context window
          "maxContextCharacters": 24000,

          // Files longer than this many lines are chunked at logical boundaries
          "maxFileLines": 800,

          // Source files larger than 10 MiB are reported as oversized without being loaded.
          "maxFileBytes": 10485760,

          // Model response bodies larger than 4 MiB are rejected before JSON parsing.
          "maxModelResponseBytes": 4194304,

          // Retries per file before it is logged and skipped
          "perFileRetryCount": 2,

          // Timeout for a single model request, in seconds; generous so a slow local or reasoning model can finish
          "requestTimeoutSeconds": 1800,

          // Optional second-opinion validation pass against a stronger model, cloud or local; null disables it.
          // WARNING: a cloud endpoint receives findings and referenced code; see DOCUMENTATION.md before enabling one.
          // apiKey must be an env:VARIABLE_NAME or file:PATH reference when the endpoint needs auth; omit it for local endpoints.
          // authentication is "bearer" by default; use "apiKey" for an Azure endpoint that expects the api-key header.
          // maxFileReads caps the validator's extra reads per file (default 5); reviewSkippedFiles also reviews files the local pass skipped (default true).
          // Example: { "endpoint": "https://api.openai.com/v1", "modelName": "gpt-5", "apiKey": "env:INFORMANT_SECOND_OPINION_KEY", "contextLines": 30, "maxFileReads": 5, "reviewSkippedFiles": true }
          // DOCUMENTATION.md describes the advanced one-to-three-profile severity router.
          "secondOpinion": null,

          // Optional report email; null disables it. Only sends when a second opinion also completed.
          // sendOn is "always", "medium" or "high". Secrets are env:VARIABLE_NAME or file:PATH references, never literals.
          // SMTP uses the built-in client: STARTTLS on 587 or plain on 25 (implicit TLS on 465 is not supported).
          // provider is "smtp" (default) or "azureCommunicationServices".
          // SMTP example:  { "provider": "smtp", "smtpHost": "smtp.internal", "smtpPort": 587, "useStartTls": true,
          //                  "from": "bugswatter@you.com", "to": ["dev@you.com"], "username": "bugswatter",
          //                  "password": "env:INFORMANT_SMTP_PASSWORD", "sendOn": "high", "attachReports": true }
          // ACS example:   { "provider": "azureCommunicationServices", "from": "DoNotReply@your-verified-domain.com",
          //                  "to": ["dev@you.com"], "acsConnectionString": "env:INFORMANT_ACS_CONNECTION", "sendOn": "high" }
          "email": null,

          "logLevel": "Information",
          "logFilePath": "logs/informant-.log",

          // true forces console logging on, false off, null auto-detects interactivity
          "consoleLogging": null
        }
        """;

    /// <summary>Creates informant.json and review-prompt.txt in <paramref name="directory"/>; refuses to overwrite existing files</summary>
    public static int Run(string directory)
    {
        string configPath = Path.Combine(directory, InformantConfig.FileName);
        string promptPath = Path.Combine(directory, PromptFileName);

        if (File.Exists(configPath) || File.Exists(promptPath))
        {
            Console.Error.WriteLine($"Refusing to overwrite an existing {InformantConfig.FileName} or {PromptFileName} in {directory}");
            return 1;
        }

        File.WriteAllText(configPath, ConfigTemplate + Environment.NewLine);
        File.WriteAllText(promptPath, DefaultReviewPrompt.Text + Environment.NewLine);

        Console.WriteLine($"Wrote {InformantConfig.FileName} and {PromptFileName} to {directory}");
        Console.WriteLine("Edit the config (repository, branch, working tree, git path, model endpoint and name), then run 'Informant verify' to prove tool-calling before the first review run. "
            + "See DOCUMENTATION.md for every option");
        
        return 0;
    }
}
