using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

// The program's own namespace is Marshal, which shadows System.Runtime.InteropServices.Marshal; this alias reaches the interop type unambiguously
using InteropMarshal = System.Runtime.InteropServices.Marshal;

namespace Marshal;

/// <summary>Registers and unregisters Marshal as a Windows service or a Linux systemd unit. On Windows the default path talks to the Service Control Manager directly through the API; pass useScExe to shell out to sc.exe instead. Both operations need administrative rights</summary>
public static class ServiceInstaller
{
    /// <summary>Service name used for both the Windows service and the systemd unit</summary>
    public const string ServiceName = "Marshal";

    private const string DisplayName = "Marshal review dispatcher";
    private const string Description = "Watches repositories and dispatches SlimShady code reviews";
    private const string SystemdUnitPath = "/etc/systemd/system/marshal.service";

    /// <summary>Registers Marshal to start automatically, launching it with the given config path</summary>
    public static int Install(string configPath, bool useScExe)
    {
        string executable = Environment.ProcessPath ?? throw new MarshalFatalException("Cannot determine the Marshal executable path for service registration");
        string fullConfigPath = Path.GetFullPath(configPath);

        return OperatingSystem.IsWindows()
            ? useScExe ? InstallWindowsWithScExe(executable, fullConfigPath) : InstallWindowsWithApi(executable, fullConfigPath)
            : InstallLinux(executable, fullConfigPath);
    }

    /// <summary>Stops and unregisters the service or unit</summary>
    public static int Remove(bool useScExe) => OperatingSystem.IsWindows() ? useScExe ? RemoveWindowsWithScExe() : RemoveWindowsWithApi() : RemoveLinux();

    /// <summary>Builds the quoted command line registered for the service, launching Marshal with its config</summary>
    public static string BuildWindowsBinPath(string executable, string configPath) => $"\"{executable}\" run --config \"{configPath}\"";

    /// <summary>Builds the systemd unit file content</summary>
    public static string BuildSystemdUnit(string executable, string configPath) => $"""
        [Unit]
        Description=Marshal review dispatcher
        After=network.target

        [Service]
        Type=notify
        ExecStart={executable} run --config {configPath}
        Restart=on-failure
        RestartSec=10

        [Install]
        WantedBy=multi-user.target
        """ + "\n";

    private static int InstallWindowsWithApi(string executable, string configPath)
    {
        nint scManager = WindowsServiceApi.OpenSCManager(null, null, WindowsServiceApi.ScManagerCreateService);
        if (scManager == 0)
        {
            return ReportWin32Failure("open the Service Control Manager");
        }

        try
        {
            nint service = WindowsServiceApi.CreateService(scManager, ServiceName, DisplayName, WindowsServiceApi.ServiceChangeConfig, WindowsServiceApi.ServiceWin32OwnProcess, WindowsServiceApi.ServiceAutoStart, WindowsServiceApi.ServiceErrorNormal,
                BuildWindowsBinPath(executable, configPath), null, 0, null, null, null);
            if (service == 0)
            {
                return ReportWin32Failure($"create service '{ServiceName}'");
            }

            try
            {
                SetServiceDescription(service);
                Console.WriteLine($"Service '{ServiceName}' installed via the Service Control Manager API. Start it with: sc.exe start {ServiceName}");
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
        nint descriptionText = InteropMarshal.StringToHGlobalUni(Description);
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
        int created = RunTool("sc.exe", ["create", ServiceName, "binPath=", BuildWindowsBinPath(executable, configPath), "start=", "auto", "DisplayName=", DisplayName]);
        if (created != 0)
        {
            Console.Error.WriteLine("Service creation failed; run from an elevated prompt");
            return created;
        }

        RunTool("sc.exe", ["description", ServiceName, Description]);
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

    private static int InstallLinux(string executable, string configPath)
    {
        try
        {
            File.WriteAllText(SystemdUnitPath, BuildSystemdUnit(executable, configPath));
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
