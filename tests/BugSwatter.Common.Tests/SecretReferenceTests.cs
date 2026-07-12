namespace BugSwatter.Common.Tests;

public sealed class SecretReferenceTests : IDisposable
{
    private const string EnvironmentVariable = "BUGSWATTER_COMMON_SECRET_TEST";

    private readonly TempDirectory _directory = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
        _directory.Dispose();
    }

    [Fact]
    public void EnvironmentReferenceResolvesFromEnvironment()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, "the-secret");
        Assert.Equal("the-secret", SecretReference.Resolve($"env:{EnvironmentVariable}"));
    }

    [Fact]
    public void FileReferenceReadsTrimsAndResolvesRelativePath()
    {
        string path = System.IO.Path.Combine(_directory.Path, "secret.txt");
        File.WriteAllText(path, "file-secret\r\n");

        Assert.Equal("file-secret", SecretReference.Resolve($"file:{path}"));
        Assert.Equal("file-secret", SecretReference.Resolve("file:secret.txt", _directory.Path));
    }

    [Fact]
    public void MissingFileResolvesToNull()
    {
        Assert.Null(SecretReference.Resolve($"file:{System.IO.Path.Combine(_directory.Path, "missing.txt")}"));
    }

    [Fact]
    public void LiteralsEmptyAndBarePrefixesAreNotReferences()
    {
        Assert.Null(SecretReference.Resolve("just-a-literal"));
        Assert.Null(SecretReference.Resolve(""));
        Assert.Null(SecretReference.Resolve(null));

        Assert.False(SecretReference.IsReference("just-a-literal"));
        Assert.False(SecretReference.IsReference("env:"));
        Assert.False(SecretReference.IsReference("file:"));
        Assert.True(SecretReference.IsReference("env:VARIABLE"));
        Assert.True(SecretReference.IsReference("file:./key"));
    }
}
