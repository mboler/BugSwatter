namespace Informant;

/// <summary>Parsed Informant command line: a command plus an optional explicit config file path</summary>
public sealed record CommandLineArguments(string Command, string? ConfigPath)
{
    /// <summary>Parses arguments of the form [command] [--config &lt;path&gt;]; the command defaults to run and unknown extras are fatal</summary>
    public static CommandLineArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? command = null;
        string? configPath = null;

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
            else if (command is null)
            {
                command = argument;
            }
            else
            {
                throw new InformantFatalException($"Unexpected argument '{argument}'. Usage: Informant [run|verify|init|help] [--config <path>]");
            }
        }

        return new CommandLineArguments(command ?? "run", configPath);
    }
}
