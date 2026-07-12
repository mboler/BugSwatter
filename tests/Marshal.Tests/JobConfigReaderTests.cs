namespace Marshal.Tests;

public sealed class JobConfigReaderTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ReadsCommentedJsonWithTrailingCommas()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, """
            {
              // local endpoint
              "modelEndpoint": "http://localhost:1234/v1",
            }
            """);

        Assert.Equal("http://localhost:1234/v1", JobConfigReader.TryReadModelEndpoint(path));
    }

    [Fact]
    public void ReadsEffectiveEnvironmentOverride()
    {
        string path = Path.Combine(_directory.Path, "informant.json");
        File.WriteAllText(path, """{ "modelEndpoint": "http://json.example/v1" }""");
        Environment.SetEnvironmentVariable("INFORMANT_ModelEndpoint", "http://environment.example/v1");
        try
        {
            Assert.Equal("http://environment.example/v1", JobConfigReader.TryReadModelEndpoint(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ModelEndpoint", null);
        }
    }
}
