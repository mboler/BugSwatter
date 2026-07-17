using BugSwatter.Common;

namespace Informant;

/// <summary>One OpenAI-compatible model available to the optional second-opinion pass</summary>
public sealed record SecondOpinionModelProfile
{
    private string _configDirectory = Directory.GetCurrentDirectory();

    /// <summary>OpenAI-compatible base URL of the validating endpoint</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Model or Azure deployment name passed to the endpoint</summary>
    public string ModelName { get; init; } = "";

    /// <summary>USD cost per million input tokens; omit with outputCostPerMillion for a local model</summary>
    public decimal? InputCostPerMillion { get; init; }

    /// <summary>USD cost per million output tokens; omit with inputCostPerMillion for a local model</summary>
    public decimal? OutputCostPerMillion { get; init; }

    /// <summary>Reference to the API key as env:VARIABLE_NAME or file:PATH; omit for an unauthenticated local endpoint</summary>
    public string? ApiKey { get; init; }

    /// <summary>How the endpoint expects an API key to be sent</summary>
    public ModelAuthentication Authentication { get; init; } = ModelAuthentication.Bearer;

    /// <summary>True when this profile declares an API-key reference</summary>
    public bool RequiresApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Cost classification and rates for this profile</summary>
    public ModelUsagePricing Pricing => new(InputCostPerMillion, OutputCostPerMillion);

    /// <summary>Reads the API key from its configured reference</summary>
    /// <returns>The key value, or null when no reference is configured or its source is unset</returns>
    public string? ResolveApiKey() => SecretReference.Resolve(ApiKey, _configDirectory);

    internal void SetConfigDirectory(string configDirectory) => _configDirectory = configDirectory;

    internal void Validate(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new InformantFatalException($"{fieldName}.endpoint must be an absolute http or https URL, got '{Endpoint}'");
        }

        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new InformantFatalException($"{fieldName}.modelName is required");
        }

        Pricing.Validate(fieldName);

        if (RequiresApiKey && !SecretReference.IsReference(ApiKey))
        {
            throw new InformantFatalException($"{fieldName}.apiKey must be an env:VARIABLE_NAME or file:PATH reference; API keys are never stored in the config file");
        }

        if (!Enum.IsDefined(Authentication))
        {
            throw new InformantFatalException($"{fieldName}.authentication is not supported: {Authentication}");
        }
    }
}

/// <summary>The one second-opinion model selected for a complete Informant run</summary>
public sealed record SecondOpinionModelSelection(string ProfileName, SecondOpinionModelProfile Model, PrimaryReviewClassification PrimaryClassification, bool UsesSeverityRouting)
{
    /// <summary>Human-readable explanation recorded in logs and reports</summary>
    public string SelectionReason => UsesSeverityRouting
        ? $"profile '{ProfileName}' selected because the highest primary candidate severity was {PrimaryClassification.DisplaySeverity}"
        : "the single configured second-opinion model was selected";
}

internal sealed record NamedSecondOpinionModel(string Name, SecondOpinionModelProfile Model);

/// <summary>Optional second-opinion validation configuration: a simple endpoint and modelName form, or an advanced form with one to three profiles
/// and a map from each run-level primary severity to one profile</summary>
public sealed record SecondOpinionConfig
{
    private static readonly string[] RequiredRouteKeys = ["none", "low", "medium", "high", "critical", "undetermined"];

    private string _configDirectory = Directory.GetCurrentDirectory();
    private bool _pathsResolved;
    private string? _promptFile;

    /// <summary>OpenAI-compatible base URL used by the simple single-model form</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>Model name used by the simple single-model form</summary>
    public string ModelName { get; init; } = "";

    /// <summary>USD cost per million input tokens for the simple form; omit with outputCostPerMillion for a local model</summary>
    public decimal? InputCostPerMillion { get; init; }

    /// <summary>USD cost per million output tokens for the simple form; omit with inputCostPerMillion for a local model</summary>
    public decimal? OutputCostPerMillion { get; init; }

    /// <summary>API-key reference used by the simple single-model form</summary>
    public string? ApiKey { get; init; }

    /// <summary>Authentication used by the simple single-model form</summary>
    public ModelAuthentication Authentication { get; init; } = ModelAuthentication.Bearer;

    /// <summary>Optional advanced model profiles; when present, one to three profiles are required and the simple endpoint fields must be absent</summary>
    public Dictionary<string, SecondOpinionModelProfile>? Profiles { get; init; }

    /// <summary>Advanced routing map from none, low, medium, high, critical and undetermined to configured profile names</summary>
    public Dictionary<string, string>? RouteBySeverity { get; init; }

    /// <summary>Context lines included on each side of a changed range when the whole file exceeds the excerpt budget</summary>
    public int ContextLines { get; init; } = 30;

    /// <summary>Maximum read_file_lines calls the validating model may make per file when its endpoint supports tool-calling</summary>
    public int MaxFileReads { get; init; } = 5;

    /// <summary>When true the validator also looks at files the local reviewer could not review</summary>
    public bool ReviewSkippedFiles { get; init; } = true;

    /// <summary>Inline validation prompt text; when null or empty the prompt file is used instead</summary>
    public string? Prompt { get; init; }

    /// <summary>Path of a file holding the validation prompt; when neither this nor the inline text is set, the built-in default applies</summary>
    public string? PromptFile
    {
        get => string.IsNullOrWhiteSpace(_promptFile) || !_pathsResolved ? _promptFile : ConfigLoader.ResolvePath(_configDirectory, _promptFile);
        init => _promptFile = value;
    }

    /// <summary>Timeout in seconds for each validation request</summary>
    public int RequestTimeoutSeconds { get; init; } = 1800;

    /// <summary>True when the advanced profile form is configured</summary>
    public bool UsesSeverityRouting => Profiles is not null;

    /// <summary>True when the simple form declares an API-key reference</summary>
    public bool RequiresApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Reads the simple form's API key from its configured reference</summary>
    /// <returns>The key value, or null when no reference is configured or its source is unset</returns>
    public string? ResolveApiKey() => SecretReference.Resolve(ApiKey, _configDirectory);

    /// <summary>Selects exactly one validator for the complete run</summary>
    /// <param name="classification">Highest candidate severity produced by the primary run</param>
    /// <returns>The selected model and the reason it was selected</returns>
    public SecondOpinionModelSelection SelectModel(PrimaryReviewClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        if (!UsesSeverityRouting)
        {
            return new SecondOpinionModelSelection("single", CreateSimpleProfile(), classification, false);
        }

        string profileName = FindValue(RouteBySeverity!, classification.RouteKey)!;
        SecondOpinionModelProfile profile = FindValue(Profiles!, profileName)!;
        return new SecondOpinionModelSelection(profileName, profile, classification, true);
    }

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

    internal IReadOnlyList<NamedSecondOpinionModel> GetConfiguredModels()
    {
        if (!UsesSeverityRouting)
        {
            return [new NamedSecondOpinionModel("single", CreateSimpleProfile())];
        }

        return [.. Profiles!.Select(pair => new NamedSecondOpinionModel(pair.Key, pair.Value))];
    }

    internal void Validate()
    {
        if (UsesSeverityRouting)
        {
            ValidateAdvanced();
        }
        else
        {
            if (RouteBySeverity is not null)
            {
                throw new InformantFatalException("secondOpinion.routeBySeverity requires advanced profiles");
            }

            CreateSimpleProfile().Validate("secondOpinion");
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
        if (Profiles is null)
        {
            return;
        }

        foreach (SecondOpinionModelProfile profile in Profiles.Values)
        {
            profile.SetConfigDirectory(configDirectory);
        }
    }

    private void ValidateAdvanced()
    {
        if (!string.IsNullOrWhiteSpace(Endpoint) || !string.IsNullOrWhiteSpace(ModelName) || InputCostPerMillion is not null || OutputCostPerMillion is not null
            || !string.IsNullOrWhiteSpace(ApiKey) || Authentication != ModelAuthentication.Bearer)
        {
            throw new InformantFatalException("secondOpinion cannot mix the simple endpoint, modelName, pricing, apiKey or authentication fields with advanced profiles");
        }

        if (Profiles is not { Count: >= 1 and <= 3 })
        {
            throw new InformantFatalException($"secondOpinion.profiles must contain between one and three models, got {Profiles?.Count ?? 0}");
        }

        EnsureUniqueKeys(Profiles.Keys, "secondOpinion.profiles");
        foreach ((string name, SecondOpinionModelProfile profile) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InformantFatalException("secondOpinion.profiles cannot contain an empty profile name");
            }

            profile.Validate($"secondOpinion.profiles.{name}");
        }

        if (RouteBySeverity is null)
        {
            throw new InformantFatalException("secondOpinion.routeBySeverity is required when profiles are configured");
        }

        EnsureUniqueKeys(RouteBySeverity.Keys, "secondOpinion.routeBySeverity");
        string[] unexpected = [.. RouteBySeverity.Keys.Where(key => !RequiredRouteKeys.Contains(key, StringComparer.OrdinalIgnoreCase))];
        if (unexpected.Length > 0)
        {
            throw new InformantFatalException($"secondOpinion.routeBySeverity contains unsupported keys: {string.Join(", ", unexpected)}");
        }

        foreach (string routeKey in RequiredRouteKeys)
        {
            string? profileName = FindValue(RouteBySeverity, routeKey);
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new InformantFatalException($"secondOpinion.routeBySeverity.{routeKey} is required");
            }

            if (FindValue(Profiles, profileName) is null)
            {
                throw new InformantFatalException($"secondOpinion.routeBySeverity.{routeKey} references unknown profile '{profileName}'");
            }
        }
    }

    private SecondOpinionModelProfile CreateSimpleProfile()
    {
        var profile = new SecondOpinionModelProfile { Endpoint = Endpoint, ModelName = ModelName, InputCostPerMillion = InputCostPerMillion, OutputCostPerMillion = OutputCostPerMillion,
            ApiKey = ApiKey, Authentication = Authentication };
        profile.SetConfigDirectory(_configDirectory);
        return profile;
    }

    private static TValue? FindValue<TValue>(IReadOnlyDictionary<string, TValue> values, string key) where TValue : class
    {
        foreach ((string candidate, TValue value) in values)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static void EnsureUniqueKeys(IEnumerable<string> keys, string fieldName)
    {
        string[] duplicates = [.. keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key)];
        if (duplicates.Length > 0)
        {
            throw new InformantFatalException($"{fieldName} contains duplicate names that differ only by case: {string.Join(", ", duplicates)}");
        }
    }
}
