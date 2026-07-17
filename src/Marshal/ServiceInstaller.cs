using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BugSwatter.Common;

// The program's own namespace is Marshal, which shadows System.Runtime.InteropServices.Marshal; this alias reaches the interop type unambiguously
using InteropMarshal = System.Runtime.InteropServices.Marshal;

namespace Marshal;

/// <summary>Registers and unregisters Marshal as a Windows service or a Linux systemd unit
/// On Windows the default path uses the Service Control Manager API; useScExe selects sc.exe instead
/// Both operations need administrative rights</summary>
public static class ServiceInstaller
{
    /// <summary>Service name used for both the Windows service and the systemd unit</summary>
    public const string ServiceName = "Marshal";

    /// <summary>Display name shown for the service in Windows management tools</summary>
    public const string WindowsServiceDisplayName = "BugSwatter Marshal Service";

    /// <summary>Description shown for the service in Windows management tools</summary>
    public const string WindowsServiceDescription = "Watches repositories and dispatches Informant code reviews";

    private const string SystemdUnitPath = "/etc/systemd/system/marshal.service";

    /// <summary>Registers Marshal to start automatically, launching it with the given config path and optional service account</summary>
    public static int Install(string configPath, bool useScExe, string? serviceUser, string? servicePasswordReference)
    {
        string executable = Environment.ProcessPath ?? throw new MarshalFatalException("Cannot determine the Marshal executable path for service registration");
        string fullConfigPath = Path.GetFullPath(configPath);
        MarshalConfig.Load(fullConfigPath);

        if (OperatingSystem.IsWindows())
        {
            if (useScExe && serviceUser is not null)
            {
                throw new MarshalFatalException("Custom service accounts require the default Service Control Manager API; remove --use-sc");
            }

            string? servicePassword = ResolveServicePassword(servicePasswordReference, fullConfigPath);
            return useScExe ? InstallWindowsWithScExe(executable, fullConfigPath) : InstallWindowsWithApi(executable, fullConfigPath, serviceUser, servicePassword);
        }

        if (servicePasswordReference is not null)
        {
            throw new MarshalFatalException("Linux systemd services use User= and do not accept --service-password");
        }

        return InstallLinux(executable, fullConfigPath, serviceUser);
    }

    /// <summary>Stops and unregisters the service or unit</summary>
    public static int Remove(bool useScExe) => OperatingSystem.IsWindows() ? useScExe ? RemoveWindowsWithScExe() : RemoveWindowsWithApi() : RemoveLinux();

    /// <summary>Builds the quoted command line registered for the service, launching Marshal with its config</summary>
    public static string BuildWindowsBinPath(string executable, string configPath) => $"\"{executable}\" run --config \"{configPath}\"";

    /// <summary>Builds the systemd unit file content, quoting paths and optionally setting User=</summary>
    public static string BuildSystemdUnit(string executable, string configPath, string? serviceUser = null)
    {
        var lines = new List<string>
        {
            "[Unit]",
            "Description=Marshal review dispatcher",
            "After=network.target",
            "",
            "[Service]",
            "Type=notify"
        };

        if (serviceUser is not null)
        {
            lines.Add($"User={QuoteSystemdArgument(serviceUser)}");
        }

        lines.Add($"ExecStart={QuoteSystemdArgument(executable)} run --config {QuoteSystemdArgument(configPath)}");
        lines.Add("Restart=on-failure");
        lines.Add("RestartSec=10");
        lines.Add("");
        lines.Add("[Install]");
        lines.Add("WantedBy=multi-user.target");
        return string.Join('\n', lines) + "\n";
    }

    private static int InstallWindowsWithApi(string executable, string configPath, string? serviceUser, string? servicePassword)
    {
        nint scManager = WindowsServiceApi.OpenSCManager(null, null, WindowsServiceApi.ScManagerCreateService);
        if (scManager == 0)
        {
            return ReportWin32Failure("open the Service Control Manager");
        }

        try
        {
            nint service = WindowsServiceApi.CreateService(scManager, ServiceName, WindowsServiceDisplayName, WindowsServiceApi.ServiceChangeConfig, WindowsServiceApi.ServiceWin32OwnProcess,
                WindowsServiceApi.ServiceAutoStart, WindowsServiceApi.ServiceErrorNormal, BuildWindowsBinPath(executable, configPath), null, 0, null, serviceUser, servicePassword);
            if (service == 0)
            {
                return ReportWin32Failure($"create service '{ServiceName}'");
            }

            try
            {
                SetServiceDescription(service);
                Console.WriteLine($"Service '{ServiceName}' installed via the Service Control Manager API as {serviceUser ?? "LocalSystem"}. Start it with: sc.exe start {ServiceName}");
                return 0;
            }
            finally
            {
                WindowsServiceApi.CloseServiceHandle(service);
            }
        }
        finally
        {
            WindowsServiceApi.CloseServiceHandle(scManager);
        }
    }

    private static int RemoveWindowsWithApi()
    {
        nint scManager = WindowsServiceApi.OpenSCManager(null, null, WindowsServiceApi.ScManagerConnect);
        if (scManager == 0)
        {
            return ReportWin32Failure("open the Service Control Manager");
        }

        try
        {
            nint service = WindowsServiceApi.OpenService(scManager, ServiceName, WindowsServiceApi.Delete | WindowsServiceApi.ServiceStop);
            if (service == 0)
            {
                return ReportWin32Failure($"open service '{ServiceName}'");
            }

            try
            {
                // Best effort stop; a service that is not running rejects the control and that is fine
                var status = new WindowsServiceApi.ServiceStatus();
                WindowsServiceApi.ControlService(service, WindowsServiceApi.ServiceControlStop, ref status);

                if (!WindowsServiceApi.DeleteService(service))
                {
                    return ReportWin32Failure($"delete service '{ServiceName}'");
                }

                Console.WriteLine($"Service '{ServiceName}' removed");
                return 0;
            }
            finally
            {
                WindowsServiceApi.CloseServiceHandle(service);
            }
        }
        finally
        {
            WindowsServiceApi.CloseServiceHandle(scManager);
        }
    }

    private static void SetServiceDescription(nint service)
    {
        // SERVICE_DESCRIPTION is a struct holding one string pointer, so the address of this pointer is the struct pointer
        nint descriptionText = InteropMarshal.StringToHGlobalUni(WindowsServiceDescription);
        try
        {
            WindowsServiceApi.ChangeServiceConfig2(service, WindowsServiceApi.ServiceConfigDescription, ref descriptionText);
        }
        finally
        {
            InteropMarshal.FreeHGlobal(descriptionText);
        }
    }

    private static int ReportWin32Failure(string action)
    {
        int error = InteropMarshal.GetLastWin32Error();
        string hint = error switch
        {
            WindowsServiceApi.ErrorAccessDenied => "run from an elevated prompt",
            WindowsServiceApi.ErrorServiceExists => "the service already exists; remove it first",
            WindowsServiceApi.ErrorServiceDoesNotExist => "the service is not installed",
            _ => new Win32Exception(error).Message
        };

        Console.Error.WriteLine($"Could not {action}: {hint} (Win32 error {error})");
        return 1;
    }

    private static int InstallWindowsWithScExe(string executable, string configPath)
    {
        int created = RunTool("sc.exe", ["create", ServiceName, "binPath=", BuildWindowsBinPath(executable, configPath), "start=", "auto", "DisplayName=", WindowsServiceDisplayName]);
        if (created != 0)
        {
            Console.Error.WriteLine("Service creation failed; run from an elevated prompt");
            return created;
        }

        RunTool("sc.exe", ["description", ServiceName, WindowsServiceDescription]);
        Console.WriteLine($"Service '{ServiceName}' installed via sc.exe. Start it with: sc.exe start {ServiceName}");
        return 0;
    }

    private static int RemoveWindowsWithScExe()
    {
        RunTool("sc.exe", ["stop", ServiceName]);
        int deleted = RunTool("sc.exe", ["delete", ServiceName]);
        Console.WriteLine(deleted == 0 ? $"Service '{ServiceName}' removed" : "Service removal failed; run from an elevated prompt");
        return deleted;
    }

    private static int InstallLinux(string executable, string configPath, string? serviceUser)
    {
        try
        {
            File.WriteAllText(SystemdUnitPath, BuildSystemdUnit(executable, configPath, serviceUser));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not write {SystemdUnitPath}: {ex.Message}. Run as root");
            return 1;
        }

        RunTool("systemctl", ["daemon-reload"]);
        int enabled = RunTool("systemctl", ["enable", ServiceName.ToLowerInvariant() + ".service"]);
        Console.WriteLine(enabled == 0 ? $"Systemd unit installed. Start it with: systemctl start {ServiceName.ToLowerInvariant()}" : "Unit written but enable failed; run as root");
        return enabled;
    }

    private static int RemoveLinux()
    {
        RunTool("systemctl", ["stop", ServiceName.ToLowerInvariant() + ".service"]);
        RunTool("systemctl", ["disable", ServiceName.ToLowerInvariant() + ".service"]);

        try
        {
            if (File.Exists(SystemdUnitPath))
            {
                File.Delete(SystemdUnitPath);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"Could not delete {SystemdUnitPath}: {ex.Message}. Run as root");
            return 1;
        }

        RunTool("systemctl", ["daemon-reload"]);
        Console.WriteLine("Systemd unit removed");
        return 0;
    }

    private static string? ResolveServicePassword(string? reference, string configPath)
    {
        if (reference is null)
        {
            return null;
        }

        if (!SecretReference.IsReference(reference))
        {
            throw new MarshalFatalException("--service-password must be an env:VARIABLE_NAME or file:PATH reference; service passwords are never accepted as command-line literals");
        }

        string configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
        string? password = SecretReference.Resolve(reference, configDirectory);
        if (string.IsNullOrEmpty(password))
        {
            throw new MarshalFatalException($"The service password reference '{reference}' is unset, empty, missing, or unreadable");
        }

        return password;
    }

    private static string QuoteSystemdArgument(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new MarshalFatalException("Systemd service arguments cannot contain nulls or line breaks");
        }

        string escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static int RunTool(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return -1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
