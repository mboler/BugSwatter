using BugSwatter.Common;

namespace Informant;

/// <summary>Opt-in controller-owned GitHub Check Run publication settings</summary>
public sealed record GitHubCheckRunConfig
{
    private string _configDirectory = Directory.GetCurrentDirectory();

    /// <summary>GitHub repository in owner/name form</summary>
    public string Repository { get; init; } = "";

    /// <summary>GitHub token reference in env:VARIABLE_NAME or file:PATH form</summary>
    public string Token { get; init; } = "";

    /// <summary>Name shown for the completed informational check</summary>
    public string Name { get; init; } = "BugSwatter review";

    /// <summary>Reads the GitHub token from its configured secret reference</summary>
    /// <returns>The token value, or null when its source is unset</returns>
    public string? ResolveToken() => SecretReference.Resolve(Token, _configDirectory);

    internal void SetConfigDirectory(string configDirectory) => _configDirectory = configDirectory;

    internal void Validate()
    {
        string[] parts = Repository.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(part => part is "." or ".." || !part.All(IsRepositoryNameCharacter)))
        {
            throw new InformantFatalException($"githubCheckRun.repository must use owner/name form, got '{Repository}'.");
        }

        if (!SecretReference.IsReference(Token))
        {
            throw new InformantFatalException("githubCheckRun.token must be an env:VARIABLE_NAME or file:PATH reference; tokens are never stored in the config file.");
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
        {
            throw new InformantFatalException("githubCheckRun.name must contain between 1 and 100 characters.");
        }
    }

    private static bool IsRepositoryNameCharacter(char character) => char.IsLetterOrDigit(character) || character is '-' or '_' or '.';
}
