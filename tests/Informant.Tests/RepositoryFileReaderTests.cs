using System.Diagnostics;

namespace Informant.Tests;

public sealed class RepositoryFileReaderTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void PrefixConfusionPathIsRejected()
    {
        string sibling = _root.Path + "-sibling";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "secret");
            string path = Path.Combine("..", Path.GetFileName(sibling), "secret.txt");

            RepositoryFileException ex = Assert.Throws<RepositoryFileException>(() => new RepositoryFileReader(_root.Path).ReadAllLines(path));

            Assert.Equal(RepositoryFileError.OutsideRoot, ex.Error);
        }
        finally
        {
            Directory.Delete(sibling, true);
        }
    }

    [Fact]
    public void FileSymbolicLinkIsRejected()
    {
        using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "secret.txt");
        string link = Path.Combine(_root.Path, "linked.txt");
        File.WriteAllText(target, "secret");
        bool created = TryCreateFileSymbolicLink(link, target);
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        try
        {
            RepositoryFileException ex = Assert.Throws<RepositoryFileException>(() => new RepositoryFileReader(_root.Path).ReadAllLines("linked.txt"));
            Assert.Equal(RepositoryFileError.ReparsePoint, ex.Error);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void DirectorySymbolicLinkIsRejected()
    {
        using var outside = new TempDirectory();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "secret");
        string link = Path.Combine(_root.Path, "linked-directory");
        bool created = TryCreateDirectorySymbolicLink(link, outside.Path);
        Assert.SkipUnless(created, "directory symbolic links are not available on this test host");
        try
        {
            RepositoryFileException ex = Assert.Throws<RepositoryFileException>(() => new RepositoryFileReader(_root.Path).ReadAllLines(Path.Combine("linked-directory", "secret.txt")));
            Assert.Equal(RepositoryFileError.ReparsePoint, ex.Error);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void WindowsJunctionIsRejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "junctions are a Windows-specific reparse point");
        using var outside = new TempDirectory();
        File.WriteAllText(Path.Combine(outside.Path, "secret.txt"), "secret");
        string junction = Path.Combine(_root.Path, "junction");
        bool created = TryCreateJunction(junction, outside.Path);
        Assert.SkipUnless(created, "a junction could not be created on this test host");
        try
        {
            RepositoryFileException ex = Assert.Throws<RepositoryFileException>(() => new RepositoryFileReader(_root.Path).ReadAllLines(Path.Combine("junction", "secret.txt")));
            Assert.Equal(RepositoryFileError.ReparsePoint, ex.Error);
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task FileThatGrowsBeyondLimitIsRejected()
    {
        string path = Path.Combine(_root.Path, "growing.txt");
        File.WriteAllLines(path, Enumerable.Repeat("a", 15000));
        await Task.Run(async () =>
        {
            while (new FileInfo(path).Length <= 64 * 1024)
            {
                await File.AppendAllTextAsync(path, new string('b', 4096));
            }
        });

        RepositoryFileException ex = Assert.Throws<RepositoryFileException>(() => new RepositoryFileReader(_root.Path, 64 * 1024).ReadAllLines("growing.txt"));
        Assert.Equal(RepositoryFileError.TooLarge, ex.Error);
    }

    [Fact]
    public void ExactLineRangeRetainsOnlyRequestedLines()
    {
        File.WriteAllLines(Path.Combine(_root.Path, "lines.txt"), Enumerable.Range(1, 1000).Select(number => $"line {number}"));

        RepositoryLineRange result = new RepositoryFileReader(_root.Path).ReadLines("lines.txt", 501, 503, 400);

        Assert.Equal(1000, result.TotalLines);
        Assert.Equal([501, 502, 503], result.Lines.Select(line => line.Number));
        Assert.Equal(["line 501", "line 502", "line 503"], result.Lines.Select(line => line.Text));
    }

    private static bool TryCreateFileSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junction);
        startInfo.ArgumentList.Add(target);
        using Process? process = Process.Start(startInfo);
        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(junction);
    }
}
