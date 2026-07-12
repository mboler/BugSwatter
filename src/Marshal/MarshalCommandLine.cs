using BugSwatter.Common;

namespace Marshal;

/// <summary>Parsed Marshal command line, including optional service-account installation settings</summary>
public sealed record MarshalCommandLine(string Command, string? ConfigPath, bool ReviewAll, bool UseScExe, string? ServiceUser, string? ServicePasswordReference)
{
    /// <summary>Parses Marshal command-line arguments; the command defaults to run</summary>
    public static MarshalCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? command = null;
        string? configPath = null;
        string? serviceUser = null;
        string? servicePasswordReference = null;
        bool reviewAll = false;
        bool useScExe = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--config" when index + 1 >= args.Length:
                    throw new MarshalFatalException("--config requires a file path argument");
                
                case "--config":
                    configPath = args[++index];
                    break;
                
                case "--review-all":
                    reviewAll = true;
                    break;
                
                case "--use-sc":
                    useScExe = true;
                    break;

                case "--service-user" when index + 1 >= args.Length:
                    throw new MarshalFatalException("--service-user requires an account name argument");

                case "--service-user":
                    serviceUser = args[++index];
                    break;

                case "--service-password" when index + 1 >= args.Length:
                    throw new MarshalFatalException("--service-password requires an env:VARIABLE_NAME or file:PATH reference");

                case "--service-password":
                    servicePasswordReference = args[++index];
                    break;
                
                default:
                {
                    if (command is not null)
                    {
                        throw new MarshalFatalException($"Unexpected argument '{argument}'. Run 'Marshal help' for usage");
                    }

                    command = argument;

                    break;
                }
            }
        }

        string resolvedCommand = command ?? "run";
        ValidateServiceOptions(resolvedCommand, useScExe, serviceUser, servicePasswordReference);
        return new MarshalCommandLine(resolvedCommand, configPath, reviewAll, useScExe, serviceUser, servicePasswordReference);
    }

    /// <summary>Returns the config path or fails with a clear message; run and install both require one</summary>
    public string RequireConfigPath() => string.IsNullOrWhiteSpace(ConfigPath) ? throw new MarshalFatalException("Marshal requires --config <path> naming its config file") : ConfigPath;

    private static void ValidateServiceOptions(string command, bool useScExe, string? serviceUser, string? servicePasswordReference)
    {
        if ((serviceUser is not null || servicePasswordReference is not null) && !command.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            throw new MarshalFatalException("--service-user and --service-password are valid only with the install command");
        }

        if (serviceUser is not null && string.IsNullOrWhiteSpace(serviceUser))
        {
            throw new MarshalFatalException("--service-user requires a non-empty account name");
        }

        if (servicePasswordReference is not null && serviceUser is null)
        {
            throw new MarshalFatalException("--service-password requires --service-user");
        }

        if (servicePasswordReference is not null && !SecretReference.IsReference(servicePasswordReference))
        {
            throw new MarshalFatalException("--service-password must be an env:VARIABLE_NAME or file:PATH reference; service passwords are never accepted as command-line literals");
        }

        if (useScExe && serviceUser is not null)
        {
            throw new MarshalFatalException("Custom service accounts require the default Service Control Manager API; remove --use-sc");
        }
    }
}
