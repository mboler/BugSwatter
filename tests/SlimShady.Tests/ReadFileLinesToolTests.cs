using System.Text.Json;

namespace SlimShady.Tests;

public sealed class ReadFileLinesToolTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void ReturnsNumberedLinesForValidRange()
    {
        WriteLines("file.txt", "alpha", "bravo", "charlie", "delta", "echo");
        string result = CreateTool().Execute("file.txt", 2, 4);
        Assert.Contains("     2 | bravo", result);
        Assert.Contains("     4 | delta", result);
        Assert.DoesNotContain("alpha", result);
        Assert.DoesNotContain("echo", result);
        Assert.DoesNotContain("note:", result);
    }

    [Fact]
    public void ClampsEndBeyondFileWithNote()
    {
        WriteLines("file.txt", "alpha", "bravo", "charlie");
        string result = CreateTool().Execute("file.txt", 2, 99);
        Assert.Contains("note: returning lines 2-3", result);
        Assert.Contains("     3 | charlie", result);
    }

    [Fact]
    public void CapsOversizedRequestsWithNote()
    {
        WriteLines("big.txt", Enumerable.Range(1, 900).Select(i => $"line {i}").ToArray());
        string result = CreateTool().Execute("big.txt", 1, 900);
        Assert.Contains($"at most {ReadFileLinesTool.MaxLinesPerCall} lines", result);
        Assert.Contains($"   {ReadFileLinesTool.MaxLinesPerCall} | line {ReadFileLinesTool.MaxLinesPerCall}", result);
        Assert.DoesNotContain($"| line {ReadFileLinesTool.MaxLinesPerCall + 1}", result);
    }

    [Fact]
    public void SubdirectoryForwardSlashPathWorks()
    {
        WriteLines(Path.Combine("sub", "inner.txt"), "nested content");
        string result = CreateTool().Execute("sub/inner.txt", 1, 1);
        Assert.Contains("nested content", result);
    }

    [Fact]
    public void RelativeTraversalIsRejected()
    {
        WriteLines("file.txt", "safe");
        string result = CreateTool().Execute("../escape.txt", 1, 5);
        Assert.Contains("outside the allowed read root", GetError(result));
    }

    [Fact]
    public void AbsolutePathOutsideRootIsRejected()
    {
        using var outside = new TempDirectory();
        string outsideFile = Path.Combine(outside.Path, "secret.txt");
        File.WriteAllText(outsideFile, "secret");
        string result = CreateTool().Execute(outsideFile, 1, 1);
        Assert.Contains("outside the allowed read root", GetError(result));
    }

    [Fact]
    public void MissingFileReturnsStructuredError()
    {
        string result = CreateTool().Execute("nope.txt", 1, 1);
        Assert.Contains("file not found", GetError(result));
    }

    [Theory]
    [InlineData(0, 5, "start_line must be 1 or greater")]
    [InlineData(5, 2, "must be greater than or equal to start_line")]
    public void InvalidRangesReturnStructuredErrors(int start, int end, string expected)
    {
        WriteLines("file.txt", "one", "two");
        Assert.Contains(expected, GetError(CreateTool().Execute("file.txt", start, end)));
    }

    [Fact]
    public void StartBeyondEndOfFileReturnsStructuredError()
    {
        WriteLines("file.txt", "one", "two");
        string result = CreateTool().Execute("file.txt", 10, 12);
        Assert.Contains("beyond the end of the file", GetError(result));
    }

    [Fact]
    public void BinaryFileReturnsStructuredError()
    {
        File.WriteAllBytes(Path.Combine(_root.Path, "blob.bin"), [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        string result = CreateTool().Execute("blob.bin", 1, 1);
        Assert.Contains("binary", GetError(result));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("""{"path": "x.txt", "start_line": "not a number", "end_line": 2}""")]
    public void MalformedArgumentsReturnStructuredError(string arguments)
    {
        Assert.Contains("invalid arguments", GetError(CreateTool().ExecuteRaw(arguments)));
    }

    [Fact]
    public void ExecuteRawHappyPathReads()
    {
        WriteLines("file.txt", "alpha", "bravo");
        string result = CreateTool().ExecuteRaw(StubHttpMessageHandler.ReadArguments("file.txt", 1, 2));
        Assert.Contains("     1 | alpha", result);
    }

    private ReadFileLinesTool CreateTool() => new(_root.Path);

    private void WriteLines(string relativePath, params string[] lines)
    {
        string fullPath = Path.Combine(_root.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllLines(fullPath, lines);
    }

    private static string GetError(string toolResult)
    {
        using var document = JsonDocument.Parse(toolResult);
        return document.RootElement.GetProperty("error").GetString()!;
    }
}
