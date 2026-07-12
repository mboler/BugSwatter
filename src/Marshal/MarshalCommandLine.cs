namespace Marshal;

/// <summary>Parsed Marshal command line: a command, the config path, the review-everything-now flag, and the sc.exe installer selector</summary>
public sealed record MarshalCommandLine(string Command, string? ConfigPath, bool ReviewAll, bool UseScExe)
{
    /// <summary>Parses arguments of the form [run|install|remove|help] [--config &lt;path&gt;] [--review-all] [--use-sc]; the command defaults to run</summary>
    public static MarshalCommandLine Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? command = null;
        string? configPath = null;
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
                
                default:
                {
                    if (command is not null)
                    {
                        throw new MarshalFatalException($"Unexpected argument '{argument}'. Usage: Marshal [run|install|remove|help] [--config <path>] [--review-all] [--use-sc]");
                    }

                    command = argument;

                    break;
                }
            }
        }

        return new MarshalCommandLine(command ?? "run", configPath, reviewAll, useScExe);
    }

    /// <summary>Returns the config path or fails with a clear message; run and install both require one</summary>
    public string RequireConfigPath() => string.IsNullOrWhiteSpace(ConfigPath) ? throw new MarshalFatalException("Marshal requires --config <path> naming its config file") : ConfigPath;
}
