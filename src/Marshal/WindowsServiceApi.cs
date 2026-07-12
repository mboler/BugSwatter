using System.Runtime.InteropServices;

namespace Marshal;

/// <summary>Direct Service Control Manager bindings so Windows service registration works without shelling out to sc.exe. Native names and signatures follow the Windows API, per the PInvoke naming exception in the coding standards</summary>
internal static partial class WindowsServiceApi
{
    /// <summary>SC_MANAGER_CONNECT access right</summary>
    public const uint ScManagerConnect = 0x0001;

    /// <summary>SC_MANAGER_CREATE_SERVICE access right</summary>
    public const uint ScManagerCreateService = 0x0002;

    /// <summary>SERVICE_WIN32_OWN_PROCESS service type</summary>
    public const uint ServiceWin32OwnProcess = 0x0010;

    /// <summary>SERVICE_AUTO_START start type</summary>
    public const uint ServiceAutoStart = 0x0002;

    /// <summary>SERVICE_ERROR_NORMAL error control</summary>
    public const uint ServiceErrorNormal = 0x0001;

    /// <summary>DELETE access right</summary>
    public const uint Delete = 0x10000;

    /// <summary>SERVICE_STOP access right</summary>
    public const uint ServiceStop = 0x0020;

    /// <summary>SERVICE_CHANGE_CONFIG access right</summary>
    public const uint ServiceChangeConfig = 0x0002;

    /// <summary>SERVICE_CONTROL_STOP control code</summary>
    public const uint ServiceControlStop = 0x0001;

    /// <summary>SERVICE_CONFIG_DESCRIPTION info level for ChangeServiceConfig2</summary>
    public const uint ServiceConfigDescription = 0x0001;

    /// <summary>ERROR_ACCESS_DENIED</summary>
    public const int ErrorAccessDenied = 5;

    /// <summary>ERROR_SERVICE_EXISTS</summary>
    public const int ErrorServiceExists = 1073;

    /// <summary>ERROR_SERVICE_DOES_NOT_EXIST</summary>
    public const int ErrorServiceDoesNotExist = 1060;

    /// <summary>SERVICE_STATUS structure passed to ControlService</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        /// <summary>dwServiceType</summary>
        public uint ServiceType;

        /// <summary>dwCurrentState</summary>
        public uint CurrentState;

        /// <summary>dwControlsAccepted</summary>
        public uint ControlsAccepted;

        /// <summary>dwWin32ExitCode</summary>
        public uint Win32ExitCode;

        /// <summary>dwServiceSpecificExitCode</summary>
        public uint ServiceSpecificExitCode;

        /// <summary>dwCheckPoint</summary>
        public uint CheckPoint;

        /// <summary>dwWaitHint</summary>
        public uint WaitHint;
    }

    /// <summary>Opens the Service Control Manager database</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    // ReSharper disable once InconsistentNaming
    public static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    /// <summary>Creates a service entry in the SCM database</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateService(nint scManager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPathName, string? loadOrderGroup, nint tagId, string? dependencies,
        string? serviceStartName, string? password);

    /// <summary>Opens an existing service</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenService(nint scManager, string serviceName, uint desiredAccess);

    /// <summary>Marks a service for deletion</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteService(nint service);

    /// <summary>Sends a control code, used here for a best-effort stop before removal</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ControlService(nint service, uint control, ref ServiceStatus status);

    /// <summary>Sets the service description via SERVICE_CONFIG_DESCRIPTION; the struct holds one pointer to the text</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig2(nint service, uint infoLevel, ref nint info);

    /// <summary>Closes an SCM or service handle</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);
}
