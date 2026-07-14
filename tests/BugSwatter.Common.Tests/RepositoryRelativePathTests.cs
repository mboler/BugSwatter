namespace BugSwatter.Common.Tests;

/// <summary>Tests filesystem-independent repository path normalization</summary>
public sealed class RepositoryRelativePathTests
{
    /// <summary>Verifies safe relative paths use a stable manifest representation</summary>
    [Theory]
    [InlineData("src/Foo.cs", "src/Foo.cs")]
    [InlineData("src\\Foo.cs", "src/Foo.cs")]
    [InlineData("./src//Foo.cs", "src/Foo.cs")]
    public void NormalizesSafeRelativePaths(string path, string expected)
    {
        Assert.Equal(expected, RepositoryRelativePath.Normalize(path));
    }

    /// <summary>Verifies absolute and parent-traversal paths are rejected on every host platform</summary>
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\server\\share\\secret.txt")]
    [InlineData("C:\\secret.txt")]
    public void RejectsUnsafePaths(string path)
    {
        Assert.False(RepositoryRelativePath.TryNormalize(path, out string normalized));
        Assert.Empty(normalized);
    }
}
