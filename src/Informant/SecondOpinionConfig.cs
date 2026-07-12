using BugSwatter.Common;

namespace Informant;

/// <summary>Configuration for the optional second-opinion validation pass against a stronger model, cloud or local. When this block is absent from the config, no second pass runs. An API key is never stored in config: the apiKey field must be an env:VARIABLE_NAME reference when set, and may be omitted entirely for local endpoints that need no authentication</summary>
public sealed record SecondOpinionConfig
{
    private string _configDirectory = Directory.GetCurrentDirectory();
    private bool _pathsResolved;
    private string? _promptFile;

    /// <summary>OpenAI-compatible base URL of the validating endpoint, cloud (for example https://api.openai.com/v1) or local (for example http://192.0.2.13:1234/v1)</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Model name passed to the endpoint</summary>
    public string ModelName { get; init; } = "";

    /// <summary>Reference to the API key, in the form env:VARIABLE_NAME or file:PATH; literal keys are rejected, and null or empty means the endpoint needs no authentication</summary>
    public string? ApiKey { get; init; }

    /// <summary>Context lines included on each side of a changed range when the whole file exceeds the excerpt budget</summary>
    public int ContextLines { get; init; } = 30;

    /// <summary>Maximum read_file_lines calls the validating model may make per file when its endpoint supports tool-calling; caps cost so a capable model cannot pull the whole tree in one night</summary>
    public int MaxFileReads { get; init; } = 5;

    /// <summary>When true the validator also looks at files the local reviewer could not review (skipped or errored), reviewing them fresh rather than validating findings; this adds cost, so set false to validate only files that produced findings</summary>
    public bool ReviewSkippedFiles { get; init; } = true;

    /// <summary>Inline validation prompt text; when null or empty the prompt file is used instead</summary>
    public string? Prompt { get; init; }

    /// <summary>Path of a file holding the validation prompt; when neither this nor the inline text is set, the built-in default applies</summary>
    public string? PromptFile
    {
        get => string.IsNullOrWhiteSpace(_promptFile) || !_pathsResolved ? _promptFile : ConfigLoader.ResolvePath(_configDirectory, _promptFile);
        init => _promptFile = value;
    }

    /// <summary>Timeout in seconds for a single validation request; generous by default because a strong or reasoning model can legitimately think for many minutes on one file, which is acceptable for an unattended run</summary>
    public int RequestTimeoutSeconds { get; init; } = 1800;

    /// <summary>True when the endpoint requires an API key, meaning an apiKey reference was configured</summary>
    public bool RequiresApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Reads the API key from its env: or file: reference</summary>
    /// <returns>The key value, or null when no reference is configured or the source is not set</returns>
    public string? ResolveApiKey() => SecretReference.Resolve(ApiKey, _configDirectory);

    /// <summary>Returns the validation prompt: inline text when set, otherwise the prompt file contents, otherwise the built-in default</summary>
    public string ResolvePrompt()
    {
        if (!string.IsNullOrWhiteSpace(Prompt))
        {
            return Prompt;
        }

        if (!string.IsNullOrWhiteSpace(PromptFile))
        {
            string promptPath = PromptFile;
            if (!File.Exists(promptPath))
            {
                throw new InformantFatalException($"Second-opinion prompt file not found: {promptPath}");
            }

            string text = File.ReadAllText(promptPath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return DefaultSecondOpinionPrompt.Text;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InformantFatalException($"secondOpinion.endpoint must be an absolute http or https URL, got '{Endpoint}'");
        }

        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new InformantFatalException("secondOpinion.modelName is required");
        }

        if (RequiresApiKey && !SecretReference.IsReference(ApiKey))
        {
            throw new InformantFatalException("secondOpinion.apiKey must be an env:VARIABLE_NAME or file:PATH reference; API keys are never stored in the config file. Omit the field for local endpoints that need no authentication");
        }

        if (RequestTimeoutSeconds <= 0)
        {
            throw new InformantFatalException($"secondOpinion.requestTimeoutSeconds must be greater than zero, got {RequestTimeoutSeconds}");
        }

        if (ContextLines <= 0)
        {
            throw new InformantFatalException($"secondOpinion.contextLines must be greater than zero, got {ContextLines}");
        }

        if (MaxFileReads < 0)
        {
            throw new InformantFatalException($"secondOpinion.maxFileReads cannot be negative, got {MaxFileReads}");
        }
    }

    internal void SetConfigDirectory(string configDirectory)
    {
        _configDirectory = configDirectory;
        _pathsResolved = true;
    }
}
