using Serilog;
using Serilog.Events;

namespace BugSwatter.Common;

/// <summary>Creates the Serilog logger shared by both executables: a rolling file sink that is always on, plus a console sink when running interactively or forced on</summary>
public static class LoggingSetup
{
    /// <summary>Builds and assigns the global logger; returns whether the console sink was enabled</summary>
    /// <param name="logLevel">Minimum level name (Verbose, Debug, Information, Warning, Error, Fatal); unrecognized values fall back to Information</param>
    /// <param name="logFilePath">Rolling log file path; a file per day is written next to it</param>
    /// <param name="consoleLogging">Forces the console sink on (true) or off (false); null auto-detects from output redirection</param>
    public static bool Initialize(string logLevel, string logFilePath, bool? consoleLogging)
    {
        if (!Enum.TryParse(logLevel, true, out LogEventLevel level))
        {
            level = LogEventLevel.Information;
        }

        // Under a service manager or scheduler output is redirected, so the console sink switches off automatically unless the config forces it
        bool console = consoleLogging ?? !Console.IsOutputRedirected;

        LoggerConfiguration loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day);

        if (console)
        {
            loggerConfiguration = loggerConfiguration.WriteTo.Console();
        }

        Log.Logger = loggerConfiguration.CreateLogger();
        return console;
    }
}
