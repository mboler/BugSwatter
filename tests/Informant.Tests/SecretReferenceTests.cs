using BugSwatter.Common;

namespace Informant.Tests;

public sealed class SecretReferenceTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void EnvReferenceResolvesFromTheEnvironment()
    {
        Environment.SetEnvironmentVariable("INFORMANT_SECRETREF_TEST", "the-secret");
        try
        {
            Assert.Equal("the-secret", SecretReference.Resolve("env:INFORMANT_SECRETREF_TEST"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_SECRETREF_TEST", null);
        }
    }

    [Fact]
    public void FileReferenceReadsAndTrimsTheFile()
    {
        string path = Path.Combine(_directory.Path, "secret.txt");
        File.WriteAllText(path, "file-secret\r\n");

        Assert.Equal("file-secret", SecretReference.Resolve($"file:{path}"));
    }

    [Fact]
    public void MissingFileResolvesToNull()
    {
        Assert.Null(SecretReference.Resolve($"file:{Path.Combine(_directory.Path, "nope.txt")}"));
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
        Assert.True(SecretReference.IsReference("env:FOO"));
        Assert.True(SecretReference.IsReference("file:./key"));
    }
}
