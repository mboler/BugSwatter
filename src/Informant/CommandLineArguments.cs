namespace Informant;

/// <summary>Optional machine-readable progress output used by supervisors and scripts</summary>
public enum ProgressOutput
{
    /// <summary>Keep the normal human-readable console and log output only</summary>
    None,

    /// <summary>Write versioned, prefixed JSON progress snapshots to stdout in addition to normal output</summary>
    Json
}

/// <summary>Parsed Informant command line: a command, an optional explicit config file path, and optional progress output</summary>
public sealed record CommandLineArguments(string Command, string? ConfigPath, ProgressOutput ProgressOutput)
{
    /// <summary>Parses arguments of the form [command] [--config &lt;path&gt;] [--progress json]; the command defaults to run and unknown extras are fatal</summary>
    public static CommandLineArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? command = null;
        string? configPath = null;
        ProgressOutput progressOutput = ProgressOutput.None;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument == "--config")
            {
                if (index + 1 >= args.Length)
                {
                    throw new InformantFatalException("--config requires a file path argument");
                }

                configPath = args[++index];
            }
            else if (argument == "--progress")
            {
                if (index + 1 >= args.Length)
                {
                    throw new InformantFatalException("--progress requires an output format argument");
                }

                string format = args[++index];
                if (!format.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InformantFatalException($"Unsupported --progress format '{format}'. The supported format is json");
                }

                progressOutput = ProgressOutput.Json;
            }
            else if (command is null)
            {
                command = argument;
            }
            else
            {
                throw new InformantFatalException($"Unexpected argument '{argument}'. Usage: Informant [run|verify|init|help] [--config <path>] [--progress json]");
            }
        }

        return new CommandLineArguments(command ?? "run", configPath, progressOutput);
    }
}
