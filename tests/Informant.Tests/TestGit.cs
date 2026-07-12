namespace Informant.Tests;

/// <summary>Locates a git executable for the integration tests</summary>
internal static class TestGit
{
    /// <summary>Full path of the git executable used by tests</summary>
    public static string ExecutablePath { get; } = Locate();

    private static string Locate()
    {
        const string WindowsDefault = @"C:\Program Files\Git\cmd\git.exe";
        if (OperatingSystem.IsWindows() && File.Exists(WindowsDefault))
        {
            return WindowsDefault;
        }
        string executableName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("git executable not found; the integration tests require git");
    }
}
