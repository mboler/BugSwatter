using BugSwatter.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Marshal;

/// <summary>Entry point: command parsing, service registration, and host construction. Marshal runs identically as a console app, a Windows service, or a systemd service; the hosting integrations detect their context</summary>
internal static class Program
{
    private static bool _loggingReady;
    private static bool _consoleLogging;

    private static async Task<int> Main(string[] args)
    {
        MarshalCommandLine commandLine;
        try
        {
            commandLine = MarshalCommandLine.Parse(args);

            switch (commandLine.Command)
            {
                case "--help" or "-h" or "help":
                    PrintUsage();
                    return 0;

                case "install":
                    return ServiceInstaller.Install(commandLine.RequireConfigPath(), commandLine.UseScExe);

                case "remove":
                    return ServiceInstaller.Remove(commandLine.UseScExe);

                case "validate":
                    return await MarshalValidateCommand.RunAsync(commandLine.RequireConfigPath());

                case "run":
                    break;

                default:
                    await Console.Error.WriteLineAsync($"Unknown command '{commandLine.Command}'");
                    PrintUsage();
                    return 1;
            }
        }
        catch (MarshalFatalException ex)
        {
            // Direct console write is intentional here: this catch runs before logging is initialized, so it is the only channel
            await Console.Error.WriteLineAsync($"Marshal fatal: {ex.Message}");

            return 1;
        }

        try
        {
            // Anchor relative config paths (log file, history) to the config's own directory. A Windows service or
            // systemd unit starts with its working directory set to a system folder, so without this the default
            // relative log and history paths would resolve there instead of beside the config
            string configPath = Path.GetFullPath(commandLine.RequireConfigPath());
            string? configDirectory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDirectory))
            {
                Directory.SetCurrentDirectory(configDirectory);
            }

            var config = MarshalConfig.Load(configPath);
            _consoleLogging = LoggingSetup.Initialize(config.LogLevel, config.LogFilePath, config.ConsoleLogging);
            _loggingReady = true;
            Log.Information("Marshal starting: {JobCount} configured jobs, per-run timeout {Timeout} minutes", config.Jobs.Count, config.PerRunTimeoutMinutes);

            var host = BuildHost(config);

            if (commandLine.ReviewAll)
            {
                ReviewQueue queue = host.Services.GetRequiredService<ReviewQueue>();
                Log.Information("--review-all: enqueueing every configured repository once");
                foreach (var job in config.Jobs)
                {
                    queue.Enqueue(job, "--review-all at startup");
                }
            }

            await host.RunAsync();
            return 0;
        }
        catch (MarshalFatalException ex)
        {
            ReportFatal(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            // catch-all so a service or unattended start always exits with a logged reason instead of an unhandled crash
            ReportFatal(ex.ToString());

            return 1;
        }
        finally
        {
            if (_loggingReady)
            {
                await Log.CloseAndFlushAsync();
            }
        }
    }

    private static void ReportFatal(string message)
    {
        if (_loggingReady)
        {
            Log.Fatal("Marshal aborted: {Reason}", message);
        }

        // The console sink already surfaces the fatal when active; write directly only when the logger cannot reach the console
        if (!_loggingReady || !_consoleLogging)
        {
            Console.Error.WriteLine($"Marshal fatal: {message}");
        }
    }

    private static IHost BuildHost(MarshalConfig config)
    {
        // With a web server the host is a Kestrel WebApplication serving health, status, dashboard and webhook routes;
        // without one it is the plain generic host, so a deployment that wants no open port keeps exactly the old behavior
        if (config.WebServer is { Enabled: true } webServer)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog();
            builder.WebHost.UseUrls($"http://{webServer.BindAddress}:{webServer.Port}");
            RegisterServices(builder.Services, config);

            WebApplication app = builder.Build();
            WebEndpoints.Map(app, config);
            Log.Information("Web server on http://{Bind}:{Port} (dashboard at /); keep this interface internal or VPN-reachable, never public", webServer.BindAddress, webServer.Port);

            return app;
        }

        HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Services.AddSerilog();
        RegisterServices(hostBuilder.Services, config);

        return hostBuilder.Build();
    }

    private static void RegisterServices(IServiceCollection services, MarshalConfig config)
    {
        services.AddWindowsService(options => options.ServiceName = ServiceInstaller.ServiceName);
        services.AddSystemd();

        services.AddSingleton(config);
        services.AddSingleton<ReviewQueue>();
        services.AddSingleton<IInformantRunner, InformantProcessRunner>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IEndpointHealthChecker, HttpEndpointHealthChecker>();
        services.AddSingleton(new BackoffTracker(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15)));
        services.AddSingleton(new RunHistoryStore(config.HistoryFilePath));
        services.AddSingleton<MarshalStatus>();
        services.AddHostedService<ReviewDispatcher>();

        if (config.Jobs.Any(job => job.Schedule is { Count: > 0 }))
        {
            services.AddHostedService<ScheduleTrigger>();
        }

        if (config.Jobs.Any(job => job.WatchPath is not null))
        {
            services.AddHostedService<FileWatchTrigger>();
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Marshal, the Informant review dispatcher");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Marshal run --config <path> [--review-all]   run in the foreground (or under a service manager)");
        Console.WriteLine("  Marshal validate --config <path>             check config, exe, secrets and every job, then exit");
        Console.WriteLine("  Marshal install --config <path> [--use-sc]   register as a Windows service or systemd unit");
        Console.WriteLine("  Marshal remove [--use-sc]                     unregister the service or unit");
        Console.WriteLine("  Marshal help                                  show this help");
        Console.WriteLine();
        Console.WriteLine("--review-all enqueues every configured repository once at startup, then keeps watching triggers");
        Console.WriteLine("--use-sc registers or removes via sc.exe instead of the Service Control Manager API (Windows only)");
    }
}
