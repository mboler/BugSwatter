namespace SlimShady.Tests;

/// <summary>Disposable temp directory for tests; contents are removed on dispose</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates a fresh unique directory under the system temp path</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slimshady-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Full path of the directory</summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            // git object files are read-only on Windows, so clear attributes before deleting
            foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // best effort cleanup; a leaked temp directory must not fail the test
        }
        catch (UnauthorizedAccessException)
        {
            // best effort cleanup; a leaked temp directory must not fail the test
        }
    }
}
