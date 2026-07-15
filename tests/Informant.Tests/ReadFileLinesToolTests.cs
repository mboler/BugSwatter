using System.Text.Json;

namespace Informant.Tests;

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

    /// <summary>Verifies seven-digit line numbers are reported without parsing a fixed-width prefix</summary>
    [Fact]
    public void SupportsSevenDigitLineNumbers()
    {
        string path = Path.Combine(_root.Path, "million.txt");
        File.WriteAllText(path, string.Join('\n', Enumerable.Repeat("x", 1_000_000)));
        var tool = new ReadFileLinesTool(_root.Path, maxFileBytes: 4 * 1024 * 1024);

        string result = tool.Execute("million.txt", 1_000_000, 1_000_000);

        using var document = JsonDocument.Parse(result);
        Assert.Equal(1_000_000, document.RootElement.GetProperty("returnedStartLine").GetInt32());
        Assert.Equal(1_000_000, document.RootElement.GetProperty("returnedEndLine").GetInt32());
    }

    /// <summary>Verifies a range beyond the final line is complete and reports end-of-file metadata</summary>
    [Fact]
    public void EndBeyondFileReturnsCompleteResponseWithEndOfFileMetadata()
    {
        WriteLines("file.txt", "alpha", "bravo", "charlie");
        string result = CreateTool().Execute("file.txt", 2, 99);
        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("complete", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("EndOfFile", document.RootElement.GetProperty("truncationReason").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("returnedEndLine").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("nextStartLine").ValueKind);
        Assert.Contains("     3 | charlie", result);
    }

    /// <summary>Verifies the line cap produces an explicit partial result and continuation line</summary>
    [Fact]
    public void LineLimitReturnsPartialResponseWithContinuationMetadata()
    {
        WriteLines("big.txt", Enumerable.Range(1, 900).Select(i => $"line {i}").ToArray());
        string result = new ReadFileLinesTool(_root.Path, maxResultCharacters: 20000).Execute("big.txt", 1, 900);
        using JsonDocument document = JsonDocument.Parse(result);
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("LineLimit", document.RootElement.GetProperty("truncationReason").GetString());
        Assert.Equal(ReadFileLinesTool.MaxLinesPerCall + 1, document.RootElement.GetProperty("nextStartLine").GetInt32());
        Assert.Contains($"   {ReadFileLinesTool.MaxLinesPerCall} | line {ReadFileLinesTool.MaxLinesPerCall}", result);
        Assert.DoesNotContain($"| line {ReadFileLinesTool.MaxLinesPerCall + 1}", result);
    }

    /// <summary>Verifies an existing path omitted from the immutable manifest cannot be read</summary>
    [Fact]
    public void ManifestRejectsExistingUntrackedFile()
    {
        WriteLines("tracked.txt", "tracked");
        WriteLines("untracked.txt", "must not be returned");
        ReadFileLinesTool tool = CreateManifestTool("tracked.txt");

        string result = tool.Execute("untracked.txt", 1, 1);

        Assert.Equal("UnknownPath", GetReasonCode(result));
        Assert.DoesNotContain("must not be returned", result);
    }

    /// <summary>Verifies content changed after manifest discovery is discarded rather than returned</summary>
    [Fact]
    public void ManifestRejectsFileChangedAfterDiscovery()
    {
        WriteLines("tracked.txt", "original");
        ReadFileLinesTool tool = CreateManifestTool("tracked.txt");
        WriteLines("tracked.txt", "modified");

        string result = tool.Execute("tracked.txt", 1, 1);

        Assert.Equal("ChangedSinceManifest", GetReasonCode(result));
        Assert.DoesNotContain("modified", result);
    }

    /// <summary>Verifies a manifested regular file replaced by a symbolic link is rejected at read time</summary>
    [Fact]
    public void ManifestedFileReplacedBySymbolicLinkIsRejectedAtReadTime()
    {
        using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "secret.txt");
        string link = Path.Combine(_root.Path, "tracked.txt");
        File.WriteAllText(target, "outside secret");
        WriteLines("tracked.txt", "original");
        ReadFileLinesTool tool = CreateManifestTool("tracked.txt");
        File.Delete(link);
        bool created = TryCreateFileSymbolicLink(link, target);
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        try
        {
            string result = tool.Execute("tracked.txt", 1, 1);

            Assert.Equal("ReparsePoint", GetReasonCode(result));
            Assert.DoesNotContain("outside secret", result);
        }
        finally
        {
            File.Delete(link);
        }
    }

    /// <summary>Verifies the largest whole-line prefix is returned with explicit continuation metadata</summary>
    [Fact]
    public void CharacterLimitReturnsLargestSafePrefixAndContinuation()
    {
        WriteLines("wide.txt", Enumerable.Range(1, 20).Select(number => $"{number}: {new string('x', 80)}").ToArray());
        var events = new List<RepositoryReadAuditEvent>();
        ReadFileLinesTool tool = CreateManifestTool("wide.txt", 700, events.Add);

        string result = tool.Execute("wide.txt", 1, 20);
        using JsonDocument document = JsonDocument.Parse(result);

        Assert.True(result.Length <= 700);
        Assert.Equal("partial", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("CharacterLimit", document.RootElement.GetProperty("truncationReason").GetString());
        Assert.True(document.RootElement.GetProperty("returnedLineCount").GetInt32() > 0);
        Assert.True(document.RootElement.GetProperty("nextStartLine").GetInt32() > 1);
        RepositoryReadAuditEvent auditEvent = Assert.Single(events);
        Assert.Equal(RepositoryReadOutcome.PartiallyServed, auditEvent.Outcome);
        Assert.Equal(result.Length, auditEvent.ResponseCharacters);
    }

    /// <summary>Verifies repeated identical rejected requests receive a terminal bounded rejection reason</summary>
    [Fact]
    public void RepeatedRejectedRequestStopsRepeatingDetailedFailure()
    {
        ReadFileLinesTool tool = CreateTool();

        tool.Execute("missing.txt", 1, 1);
        tool.Execute("missing.txt", 1, 1);
        string result = tool.Execute("missing.txt", 1, 1);

        Assert.Equal("RepeatedRejectedRequest", GetReasonCode(result));
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
        Assert.Contains("must be relative", GetError(result));
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
    public void MaximumIntegerRangeDoesNotOverflow()
    {
        WriteLines("file.txt", "one", "two");

        string result = CreateTool().Execute("file.txt", int.MaxValue, int.MaxValue);

        Assert.Contains("beyond the end of the file", GetError(result));
    }

    [Fact]
    public void BinaryFileReturnsStructuredError()
    {
        File.WriteAllBytes(Path.Combine(_root.Path, "blob.bin"), [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        string result = CreateTool().Execute("blob.bin", 1, 1);
        Assert.Contains("binary", GetError(result));
    }

    [Fact]
    public void BinaryMarkerBeyondInitialProbeReturnsStructuredError()
    {
        byte[] bytes = [.. Enumerable.Repeat((byte)'a', 9000), 0, (byte)'b'];
        File.WriteAllBytes(Path.Combine(_root.Path, "late-binary.bin"), bytes);

        string result = CreateTool().Execute("late-binary.bin", 1, 1);

        Assert.Contains("binary", GetError(result));
    }

    [Fact]
    public void OversizedFileReturnsStructuredErrorWithoutReturningContent()
    {
        File.WriteAllText(Path.Combine(_root.Path, "large.txt"), new string('x', 1025));
        string result = new ReadFileLinesTool(_root.Path, 1024).Execute("large.txt", 1, 1);

        Assert.Contains("maxFileBytes", GetError(result));
        Assert.DoesNotContain(new string('x', 100), result);
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

    private ReadFileLinesTool CreateManifestTool(string relativePath, int maxResultCharacters = ReadFileLinesTool.DefaultMaxResultCharacters,
        Action<RepositoryReadAuditEvent>? auditObserver = null)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        var reader = new RepositoryFileReader(_root.Path);
        RepositoryFileInspection inspection = reader.Inspect(relativePath);
        var entry = new RepositoryManifestEntry(normalizedPath, "100644", "blob", "object", inspection.SizeBytes, inspection.LineCount, inspection.ContentHash,
            Path.GetExtension(normalizedPath), !normalizedPath.Contains('/'), RepositoryManifestDisposition.Text);
        var manifest = new RepositoryManifest("repository", "main", reader.Root, null, "tip", ReviewMode.Changed, "2026-07-14_00-00-00", DateTimeOffset.UnixEpoch, [entry]);
        return new ReadFileLinesTool(reader, manifest, maxResultCharacters, auditObserver);
    }

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

    private static string GetReasonCode(string toolResult)
    {
        using var document = JsonDocument.Parse(toolResult);
        return document.RootElement.GetProperty("reasonCode").GetString()!;
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
}
