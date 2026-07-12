using Microsoft.Extensions.Configuration;

namespace BugSwatter.Common;

/// <summary>Loads a JSON config file and layers prefixed environment variables over it, then binds the result to a
/// config type. The environment layer follows the standard .NET convention: PREFIX plus the key path with a double
/// underscore as the section separator, so for a SLIMSHADY_ prefix, SLIMSHADY_ModelName overrides modelName and
/// SLIMSHADY_Email__SmtpHost overrides email.smtpHost. Only prefixed variables participate, so unrelated machine
/// variables never leak into the config. The JSON provider tolerates comments and trailing commas</summary>
public static class ConfigLoader
{
    /// <summary>Builds the JSON-plus-environment configuration and binds it to <typeparamref name="T"/></summary>
    /// <param name="filePath">Absolute path of the JSON config file; it must exist</param>
    /// <param name="environmentPrefix">Prefix that selects and is stripped from participating environment variables, for example "SLIMSHADY_"</param>
    public static T Load<T>(string filePath, string environmentPrefix) where T : class, new()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(filePath, optional: false)
            .AddEnvironmentVariables(environmentPrefix)
            .Build();

        return configuration.Get<T>() ?? new T();
    }
}
