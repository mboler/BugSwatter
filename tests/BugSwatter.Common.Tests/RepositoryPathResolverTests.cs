namespace BugSwatter.Common.Tests;

public sealed class RepositoryPathResolverTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void ExistingRegularFileResolvesWithinRoot()
    {
        string path = System.IO.Path.Combine(_root.Path, "source.cs");
        File.WriteAllText(path, "class Source { }");

        Assert.Equal(path, new RepositoryPathResolver(_root.Path).ResolveFile("source.cs"));
    }

    [Fact]
    public void AbsoluteAndEscapingPathsAreRejected()
    {
        var resolver = new RepositoryPathResolver(_root.Path);
        Assert.Equal(RepositoryFileError.InvalidPath, Assert.Throws<RepositoryFileException>(() => resolver.ResolveFile(_root.Path)).Error);
        Assert.Equal(RepositoryFileError.OutsideRoot, Assert.Throws<RepositoryFileException>(() => resolver.ResolveFile(System.IO.Path.Combine("..", "outside.cs"))).Error);
    }

    [Fact]
    public void MissingFileIsCategorized()
    {
        RepositoryFileException exception = Assert.Throws<RepositoryFileException>(() => new RepositoryPathResolver(_root.Path).ResolveFile("missing.cs"));
        Assert.Equal(RepositoryFileError.NotFound, exception.Error);
    }

    [Fact]
    public void SymbolicLinkIsRejected()
    {
        using var outside = new TempDirectory();
        string target = System.IO.Path.Combine(outside.Path, "target.cs");
        string link = System.IO.Path.Combine(_root.Path, "linked.cs");
        File.WriteAllText(target, "class Target { }");
        bool created = TryCreateSymbolicLink(link, target);
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        try
        {
            RepositoryFileException exception = Assert.Throws<RepositoryFileException>(() => new RepositoryPathResolver(_root.Path).ResolveFile("linked.cs"));
            Assert.Equal(RepositoryFileError.ReparsePoint, exception.Error);
        }
        finally
        {
            File.Delete(link);
        }
    }

    private static bool TryCreateSymbolicLink(string link, string target)
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
}
