using System.Text;
using System.Text.Json.Serialization;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Configuration for an Informant run. Relative operational paths resolve from the configuration file
/// directory, while destructive operations target only the absolute working-tree path declared inside it</summary>
public sealed class InformantConfig
{
    private string _configDirectory = Directory.GetCurrentDirectory();
    private bool _pathsResolved;
    private string _gitExecutablePath = "";
    private string? _allowedReadRoot;
    private string _reportDirectory = "reports";
    private string _stateFilePath = "informant.state.json";
    private string? _reviewPromptFile;
    private string _logFilePath = "logs/informant-.log";

    /// <summary>Name of the config file expected in the current working directory</summary>
    public const string FileName = "informant.json";

    /// <summary>Git remote URL of the repository to review</summary>
    public string RepositoryUrl { get; init; } = "";

    /// <summary>Branch to review</summary>
    public string Branch { get; init; } = "";

    /// <summary>Absolute path of the working tree Informant owns and refreshes destructively on every run</summary>
    public string WorkingTreePath { get; init; } = "";

    /// <summary>Path of the git executable, resolved from the configuration directory when relative</summary>
    public string GitExecutablePath
    {
        get => ResolvePath(_gitExecutablePath);
        init => _gitExecutablePath = value;
    }

    /// <summary>Directory tree the read_file_lines tool may read from; defaults to the working tree when null or empty</summary>
    public string? AllowedReadRoot
    {
        get => _allowedReadRoot;
        init => _allowedReadRoot = value;
    }

    /// <summary>Base URL of the OpenAI-compatible model endpoint, for example http://localhost:1234/v1</summary>
    public string ModelEndpoint { get; init; } = "";

    /// <summary>Model name passed to the endpoint, or * to select its single loaded LM Studio model</summary>
    public string ModelName { get; init; } = "";

    /// <summary>USD cost per million primary-model input tokens; omit with outputCostPerMillion for a local model</summary>
    public decimal? InputCostPerMillion { get; init; }

    /// <summary>USD cost per million primary-model output tokens; omit with inputCostPerMillion for a local model</summary>
    public decimal? OutputCostPerMillion { get; init; }

    /// <summary>Ordered already-running models tried after the preferred primary model suffers an unrecoverable model-layer failure</summary>
    public IReadOnlyList<FallbackModelConfig> FallbackModels { get; init; } = [];

    /// <summary>Which file set to review</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ReviewMode>))]
    public ReviewMode ReviewMode { get; init; } = ReviewMode.Changed;

    /// <summary>Whether every candidate receives deep review or full-file review may be adaptively deferred</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ReviewStrategy>))]
    public ReviewStrategy ReviewStrategy { get; init; } = ReviewStrategy.Exhaustive;

    /// <summary>Directory the run reports and changed-file lists are written to, resolved from the configuration directory when relative</summary>
    public string ReportDirectory
    {
        get => ResolvePath(_reportDirectory);
        init => _reportDirectory = value;
    }

    /// <summary>Days completed report artifacts are retained; -1 keeps them forever</summary>
    public int ReportRetentionDays { get; init; } = 31;

    /// <summary>Path of the JSON state file holding baseline SHAs keyed by repository and branch, resolved from the configuration directory when relative</summary>
    public string StateFilePath
    {
        get => ResolvePath(_stateFilePath);
        init => _stateFilePath = value;
    }

    /// <summary>Inline review prompt text; when null or empty the prompt file is used instead</summary>
    public string? ReviewPrompt { get; init; }

    /// <summary>Path of a file holding the review prompt; when neither this nor the inline text is set, the built-in default applies</summary>
    public string? ReviewPromptFile
    {
        get => string.IsNullOrWhiteSpace(_reviewPromptFile) || !_pathsResolved ? _reviewPromptFile : ConfigLoader.ResolvePath(_configDirectory, _reviewPromptFile);
        init => _reviewPromptFile = value;
    }

    /// <summary>
    /// Glob patterns for Markdown guidance appended to the review prompt, with relative patterns rooted in the working tree and absolute patterns naming exact files.
    /// </summary>
    public IReadOnlyList<string> PromptIncludeFiles { get; init; } = [];

    /// <summary>Repository-relative files, directories, or glob patterns prioritized as initial planning context without expanding the read boundary or context budget</summary>
    public IReadOnlyList<string> SeedPaths { get; init; } = [];

    /// <summary>Target character budget per review call, deliberately kept well below the model's advertised context window</summary>
    public int MaxContextCharacters { get; init; } = 24000;

    /// <summary>Line count above which a file is chunked at logical boundaries instead of fed whole</summary>
    public int MaxFileLines { get; init; } = 800;

    /// <summary>Maximum source-file size read into a review, in bytes</summary>
    public int MaxFileBytes { get; init; } = RepositoryFileReader.DefaultMaxFileBytes;

    /// <summary>Maximum model HTTP response body, in bytes</summary>
    public int MaxModelResponseBytes { get; init; } = ModelClient.DefaultMaxResponseBytes;

    /// <summary>How many times a failed file review is retried before it is logged and skipped</summary>
    public int PerFileRetryCount { get; init; } = 2;

    /// <summary>Timeout in seconds for a single model request; local and reasoning models can be slow, so this is generous by default</summary>
    public int RequestTimeoutSeconds { get; init; } = 1800;

    /// <summary>Minimum Serilog level: Verbose, Debug, Information, Warning, Error or Fatal</summary>
    public string LogLevel { get; init; } = "Information";

    /// <summary>Log file path resolved from the configuration directory when relative; a rolling file per day is written next to it</summary>
    public string LogFilePath
    {
        get => ResolvePath(_logFilePath);
        init => _logFilePath = value;
    }

    /// <summary>Forces the console sink on (true) or off (false); null auto-detects interactivity</summary>
    public bool? ConsoleLogging { get; init; }

    /// <summary>Optional second-opinion validation pass; null means the run stops after the local review as before</summary>
    public SecondOpinionConfig? SecondOpinion { get; init; }

    /// <summary>Optional report email; only sends when a Second Opinion also completed. Null disables email</summary>
    public EmailConfig? Email { get; init; }

    /// <summary>Absolute directory the read_file_lines tool is confined to</summary>
    public string ResolvedAllowedReadRoot => string.IsNullOrWhiteSpace(AllowedReadRoot) ? WorkingTreePath : ConfigLoader.ResolvePath(_configDirectory, AllowedReadRoot);

    /// <summary>Returns the preferred model followed by the configured fallbacks in attempt order</summary>
    public IReadOnlyList<PrimaryModelTarget> GetPrimaryModelTargets() =>
    [
        new PrimaryModelTarget("primary", ModelEndpoint, ModelName, false, InputCostPerMillion, OutputCostPerMillion),
        .. FallbackModels.Select(model => new PrimaryModelTarget(model.Name, model.Endpoint, model.ModelName, true, model.InputCostPerMillion, model.OutputCostPerMillion))
    ];

    /// <summary>Loads and validates the configuration from the default file name inside <paramref name="directory"/></summary>
    public static InformantConfig Load(string directory) => LoadFile(Path.Combine(directory, FileName));

    /// <summary>Loads and validates the configuration from an explicit file path</summary>
    public static InformantConfig LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InformantFatalException($"Config file not found: {path}. Run 'Informant init' to create a starter config");
        }

        InformantConfig config;
        try
        {
            string fullPath = Path.GetFullPath(path);
            config = ConfigLoader.Load<InformantConfig>(fullPath, "INFORMANT_");
            config.SetConfigDirectory(ConfigLoader.GetConfigDirectory(fullPath));
        }
        catch (Exception ex) when (ex is not InformantFatalException)
        {
            // catch-all: a malformed JSON file or a value that cannot bind to the config shape surfaces as a fatal config error
            throw new InformantFatalException($"Config file {path} could not be read: {ex.Message}", ex);
        }

        config.Validate();
        return config;
    }

    /// <summary>Assembles the selected base prompt plus every Markdown guidance file matched by <see cref="PromptIncludeFiles"/> in the working tree</summary>
    public string ResolveReviewPrompt(string workingTreePath)
    {
        if (PromptIncludeFiles is null)
        {
            throw new InformantFatalException("promptIncludeFiles must be an array; use an empty array or omit it to disable prompt includes");
        }

        var builder = new StringBuilder(ResolveBasePrompt().TrimEnd());
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var repositoryReader = new RepositoryFileReader(workingTreePath, MaxFileBytes);

        foreach (string pattern in PromptIncludeFiles)
        {
            foreach (string file in MatchIncludeFiles(workingTreePath, pattern))
            {
                if (!seen.Add(Path.GetFullPath(file)))
                {
                    continue;
                }

                string content;
                try
                {
                    content = Path.IsPathFullyQualified(pattern)
                        ? File.ReadAllText(file).Trim()
                        : string.Join(Environment.NewLine, repositoryReader.ReadAllLines(Path.GetRelativePath(workingTreePath, file))).Trim();
                }
                catch (RepositoryFileException ex)
                {
                    Log.Warning("Skipping repository prompt include {File}: {Reason}", file, ex.Message);
                    continue;
                }

                if (content.Length == 0)
                {
                    continue;
                }

                Log.Information("Appending {File} ({Length} characters) to the review prompt", Path.GetFileName(file), content.Length);
                
                builder.AppendLine();
                builder.AppendLine();
                builder.AppendLine($"Additional project guidance from {Path.GetFileName(file)}, which the reviewed repository supplies and which takes effect for this review:");
                builder.AppendLine();
                builder.AppendLine(content);
            }
        }

        return DefaultReviewPrompt.EnsureStructuredFindingsContract(builder.ToString());
    }

    private string ResolveBasePrompt()
    {
        if (!string.IsNullOrWhiteSpace(ReviewPrompt))
        {
            return ReviewPrompt;
        }

        if (!string.IsNullOrWhiteSpace(ReviewPromptFile))
        {
            string promptPath = ReviewPromptFile;
            if (!File.Exists(promptPath))
            {
                throw new InformantFatalException($"Review prompt file not found: {promptPath}");
            }

            string text = File.ReadAllText(promptPath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return DefaultReviewPrompt.Text;
    }

    private static IReadOnlyList<string> MatchIncludeFiles(string workingTreePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        if (Path.IsPathFullyQualified(pattern))
        {
            return File.Exists(pattern) ? [pattern] : [];
        }

        try
        {
            return [.. Directory.GetFiles(workingTreePath, pattern, SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException)
        {
            Log.Warning("Prompt include pattern '{Pattern}' could not be evaluated: {Reason}", pattern, ex.Message);
            return [];
        }
    }

    private void Validate()
    {
        RequireValue(RepositoryUrl, "repositoryUrl");
        RequireValue(Branch, "branch");
        RequireValue(WorkingTreePath, "workingTreePath");
        RequireValue(GitExecutablePath, "gitExecutablePath");
        RequireValue(ModelEndpoint, "modelEndpoint");
        RequireValue(ModelName, "modelName");

        if (!Path.IsPathFullyQualified(WorkingTreePath))
        {
            throw new InformantFatalException($"workingTreePath must be an absolute path, got '{WorkingTreePath}'. "
                + "Informant only operates destructively on an explicitly configured absolute tree, never on the current directory");
        }

        if (!File.Exists(GitExecutablePath))
        {
            throw new InformantFatalException($"gitExecutablePath does not exist: {GitExecutablePath}");
        }

        if (!Uri.TryCreate(ModelEndpoint, UriKind.Absolute, out Uri? endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InformantFatalException($"modelEndpoint must be an absolute http or https URL, got '{ModelEndpoint}'");
        }

        ValidateFallbackModels();
        new ModelUsagePricing(InputCostPerMillion, OutputCostPerMillion).Validate("primaryModel");
        ValidatePromptIncludeFiles();
        ValidateSeedPaths();

        if (MaxContextCharacters < ReadFileLinesTool.MinimumMaxResultCharacters * 4)
        {
            throw new InformantFatalException($"maxContextCharacters must be at least {ReadFileLinesTool.MinimumMaxResultCharacters * 4}, got {MaxContextCharacters}");
        }
        RequirePositive(MaxFileLines, "maxFileLines");
        RequirePositive(MaxFileBytes, "maxFileBytes");
        RequirePositive(MaxModelResponseBytes, "maxModelResponseBytes");
        RequirePositive(RequestTimeoutSeconds, "requestTimeoutSeconds");

        if (ReportRetentionDays != -1 && ReportRetentionDays < 1)
        {
            throw new InformantFatalException($"reportRetentionDays must be -1 to keep reports forever or at least 1 day, got {ReportRetentionDays}");
        }

        if (PerFileRetryCount < 0)
        {
            throw new InformantFatalException($"perFileRetryCount must be zero or greater, got {PerFileRetryCount}");
        }

        SecondOpinion?.Validate();
        Email?.Validate();

        if (Email is not null && SecondOpinion is null)
        {
            throw new InformantFatalException("email is configured but secondOpinion is not; email sends only after a completed second opinion, so configure secondOpinion or remove the email block");
        }
    }

    private void SetConfigDirectory(string configDirectory)
    {
        _configDirectory = configDirectory;
        _pathsResolved = true;
        SecondOpinion?.SetConfigDirectory(configDirectory);
        Email?.SetConfigDirectory(configDirectory);
    }

    private string ResolvePath(string configuredPath) => _pathsResolved ? ConfigLoader.ResolvePath(_configDirectory, configuredPath) : configuredPath;

    private void ValidateFallbackModels()
    {
        if (FallbackModels is null)
        {
            throw new InformantFatalException("fallbackModels must be an array; use an empty array or omit it to disable failover");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{ModelEndpoint.TrimEnd('/')}|{ModelName}" };

        for (int index = 0; index < FallbackModels.Count; index++)
        {
            FallbackModelConfig fallback = FallbackModels[index];
            string fieldName = $"fallbackModels[{index}]";
            fallback.Validate(fieldName);

            if (!names.Add(fallback.Name))
            {
                throw new InformantFatalException($"{fieldName}.name duplicates another fallback name: '{fallback.Name}'");
            }

            if (!targets.Add($"{fallback.Endpoint.TrimEnd('/')}|{fallback.ModelName}"))
            {
                throw new InformantFatalException($"{fieldName} duplicates an earlier endpoint and model combination");
            }
        }
    }

    private void ValidatePromptIncludeFiles()
    {
        if (PromptIncludeFiles is null)
        {
            throw new InformantFatalException("promptIncludeFiles must be an array; use an empty array or omit it to disable prompt includes");
        }

        for (int index = 0; index < PromptIncludeFiles.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(PromptIncludeFiles[index]))
            {
                throw new InformantFatalException($"promptIncludeFiles[{index}] must be a non-empty path or glob pattern");
            }
        }
    }

    private void ValidateSeedPaths()
    {
        if (SeedPaths is null)
        {
            throw new InformantFatalException("seedPaths must be an array; use an empty array or omit it to disable configured seeds");
        }

        for (int index = 0; index < SeedPaths.Count; index++)
        {
            string seedPath = SeedPaths[index];
            try
            {
                RepositoryRelativePath.Normalize(seedPath);
            }
            catch (ArgumentException ex)
            {
                throw new InformantFatalException($"seedPaths[{index}] must be a repository-relative file, directory, or glob pattern: {ex.Message}", ex);
            }
        }
    }

    private static void RequireValue(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InformantFatalException($"Config field '{fieldName}' is required and must not be empty");
        }
    }

    private static void RequirePositive(int value, string fieldName)
    {
        if (value <= 0)
        {
            throw new InformantFatalException($"Config field '{fieldName}' must be greater than zero, got {value}");
        }
    }
}
