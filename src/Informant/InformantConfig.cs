using System.Text;
using System.Text.Json.Serialization;
using BugSwatter.Common;
using Serilog;

namespace Informant;

/// <summary>Configuration for an Informant run, loaded from informant.json in the current working directory. The current directory only selects which config to load; all destructive operations target the absolute working-tree path declared inside it</summary>
public sealed class InformantConfig
{
    /// <summary>Name of the config file expected in the current working directory</summary>
    public const string FileName = "informant.json";

    /// <summary>Git remote URL of the repository to review</summary>
    public string RepositoryUrl { get; init; } = "";

    /// <summary>Branch to review</summary>
    public string Branch { get; init; } = "";

    /// <summary>Absolute path of the working tree Informant owns and refreshes destructively on every run</summary>
    public string WorkingTreePath { get; init; } = "";

    /// <summary>Full path of the git executable, for example C:\Program Files\Git\cmd\git.exe on Windows or /usr/bin/git on Linux</summary>
    public string GitExecutablePath { get; init; } = "";

    /// <summary>Directory tree the read_file_lines tool may read from; defaults to the working tree when null or empty</summary>
    public string? AllowedReadRoot { get; init; }

    /// <summary>Base URL of the OpenAI-compatible model endpoint, for example http://localhost:1234/v1</summary>
    public string ModelEndpoint { get; init; } = "";

    /// <summary>Model name passed to the endpoint</summary>
    public string ModelName { get; init; } = "";

    /// <summary>Which file set to review</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ReviewMode>))]
    public ReviewMode ReviewMode { get; init; } = ReviewMode.Changed;

    /// <summary>Directory the run reports and changed-file lists are written to</summary>
    public string ReportDirectory { get; init; } = "reports";

    /// <summary>Path of the JSON state file holding baseline SHAs keyed by repository and branch</summary>
    public string StateFilePath { get; init; } = "informant.state.json";

    /// <summary>Inline review prompt text; when null or empty the prompt file is used instead</summary>
    public string? ReviewPrompt { get; init; }

    /// <summary>Path of a file holding the review prompt; when neither this nor the inline text is set, the built-in default applies</summary>
    public string? ReviewPromptFile { get; init; }

    /// <summary>Glob patterns for Markdown guidance files appended to the review prompt. Relative patterns match at the working-tree root, so a repository's own AGENTS.md informs its review; absolute paths name exact files. Defaults to none; the starter config opts in with AGENTS.md</summary>
    public IReadOnlyList<string> PromptIncludeFiles { get; init; } = [];

    /// <summary>Target character budget per review call, deliberately kept well below the model's advertised context window</summary>
    public int MaxContextCharacters { get; init; } = 24000;

    /// <summary>Line count above which a file is chunked at logical boundaries instead of fed whole</summary>
    public int MaxFileLines { get; init; } = 800;

    /// <summary>How many times a failed file review is retried before it is logged and skipped</summary>
    public int PerFileRetryCount { get; init; } = 2;

    /// <summary>Timeout in seconds for a single model request; local and reasoning models can be slow, so this is generous by default</summary>
    public int RequestTimeoutSeconds { get; init; } = 1800;

    /// <summary>Minimum Serilog level: Verbose, Debug, Information, Warning, Error or Fatal</summary>
    public string LogLevel { get; init; } = "Information";

    /// <summary>Log file path; a rolling file per day is written next to it</summary>
    public string LogFilePath { get; init; } = "logs/informant-.log";

    /// <summary>Forces the console sink on (true) or off (false); null auto-detects interactivity</summary>
    public bool? ConsoleLogging { get; init; }

    /// <summary>Optional second-opinion validation pass; null means the run stops after the local review as before</summary>
    public SecondOpinionConfig? SecondOpinion { get; init; }

    /// <summary>Optional report email; only sends when a Second Opinion also completed. Null disables email</summary>
    public EmailConfig? Email { get; init; }

    /// <summary>Absolute directory the read_file_lines tool is confined to</summary>
    public string ResolvedAllowedReadRoot => string.IsNullOrWhiteSpace(AllowedReadRoot) ? WorkingTreePath : Path.GetFullPath(AllowedReadRoot);

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
            config = ConfigLoader.Load<InformantConfig>(path, "INFORMANT_");
        }
        catch (Exception ex) when (ex is not InformantFatalException)
        {
            // catch-all: a malformed JSON file or a value that cannot bind to the config shape surfaces as a fatal config error
            throw new InformantFatalException($"Config file {path} could not be read: {ex.Message}", ex);
        }

        config.Validate();
        return config;
    }

    /// <summary>Assembles the review prompt: the base (inline text, else prompt file, else the built-in default) plus every Markdown guidance file matched by <see cref="PromptIncludeFiles"/> in the working tree</summary>
    public string ResolveReviewPrompt(string workingTreePath)
    {
        var builder = new StringBuilder(ResolveBasePrompt().TrimEnd());
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (string pattern in PromptIncludeFiles)
        {
            foreach (string file in MatchIncludeFiles(workingTreePath, pattern))
            {
                if (!seen.Add(Path.GetFullPath(file)))
                {
                    continue;
                }

                string content = File.ReadAllText(file).Trim();
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

        return builder.ToString();
    }

    private string ResolveBasePrompt()
    {
        if (!string.IsNullOrWhiteSpace(ReviewPrompt))
        {
            return ReviewPrompt;
        }

        if (!string.IsNullOrWhiteSpace(ReviewPromptFile))
        {
            string promptPath = Path.GetFullPath(ReviewPromptFile);
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
            throw new InformantFatalException($"workingTreePath must be an absolute path, got '{WorkingTreePath}'. Informant only operates destructively on an explicitly configured absolute tree, never on the current directory");
        }

        if (!File.Exists(GitExecutablePath))
        {
            throw new InformantFatalException($"gitExecutablePath does not exist: {GitExecutablePath}");
        }

        if (!Uri.TryCreate(ModelEndpoint, UriKind.Absolute, out Uri? endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InformantFatalException($"modelEndpoint must be an absolute http or https URL, got '{ModelEndpoint}'");
        }

        RequirePositive(MaxContextCharacters, "maxContextCharacters");
        RequirePositive(MaxFileLines, "maxFileLines");
        RequirePositive(RequestTimeoutSeconds, "requestTimeoutSeconds");

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
