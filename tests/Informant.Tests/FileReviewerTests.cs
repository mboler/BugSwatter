using System.Net;
using System.Text.Json;

namespace Informant.Tests;

public sealed class FileReviewerTests : IDisposable
{
    private readonly TempDirectory _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task ReviewsWholeFileWithChangedRangeFocus()
    {
        WriteLines("src/Foo.cs", Enumerable.Range(1, 20).Select(i => $"int value{i};").ToArray());
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("Nothing of concern."));

        FileReviewResult result = await CreateReviewer(handler).ReviewAsync(new ChangedFile("src/Foo.cs", ChangeKind.Modified, [new LineRange(3, 5)]));

        Assert.True(result.FullyReviewed);
        Assert.Equal(FileReviewStatus.Reviewed, result.Status);
        Assert.Contains("Nothing of concern.", result.Findings);
        Assert.Equal(1, result.TotalChunks);
        string userPrompt = GetUserPrompt(handler.RequestBodies[0]);
        Assert.Contains("File: src/Foo.cs", userPrompt);
        Assert.Contains("Changed line ranges (1-based, inclusive): 3-5", userPrompt);
        Assert.Contains("     3 | int value3;", userPrompt);
        Assert.Contains("    20 | int value20;", userPrompt);
    }

    [Fact]
    public async Task StructuredCandidateSeverityIsCapturedAndHiddenFromTheProseReport()
    {
        WriteLines("src/Foo.cs", "class Foo { }");
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("""
            High-severity issue in Foo.cs.
            ```json
            { "findings": [ { "file": "src/Foo.cs", "line": 1, "severity": "high", "summary": "bug" } ] }
            ```
            """));

        FileReviewResult result = await CreateReviewer(handler).ReviewAsync(new ChangedFile("src/Foo.cs", ChangeKind.Modified, [new LineRange(1, 1)]));

        Assert.True(result.CandidateSeverityDetermined);
        Assert.Equal(Severity.High, result.CandidateSeverity);
        Assert.Contains("High-severity issue", result.Findings);
        Assert.DoesNotContain("```json", result.Findings);
    }

    [Fact]
    public async Task AddedFileIsWhollyTheSubject()
    {
        WriteLines("New.cs", "class New { }");
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("ok"));
        await CreateReviewer(handler).ReviewAsync(new ChangedFile("New.cs", ChangeKind.Added, [new LineRange(1, 1)]));
        Assert.Contains("newly added; the entire file is the review subject", GetUserPrompt(handler.RequestBodies[0]));
    }

    [Fact]
    public async Task DeletedFileIsReviewedFromBaselineWithRemovalGuidance()
    {
        using var repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("removed.cs", "public static class RemovedApi { }\n", "add removed API");
        await repository.DeleteFileAsync("removed.cs", "delete removed API");
        string treePath = Path.Combine(repository.Root, "tree");
        var git = new GitRunner(TestGit.ExecutablePath);
        await new WorkingTreeManager(git, repository.RemotePath, "main", treePath).EnsureFreshTreeAsync();
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("Deletion may break callers."));
        var reviewer = new FileReviewer(new ToolCallLoop(new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5)),
            new ReadFileLinesTool(treePath), 100000), treePath, "system prompt", 800, 100000, 0, RepositoryFileReader.DefaultMaxFileBytes, git);

        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile("removed.cs", ChangeKind.Deleted, [], baseline));

        Assert.True(result.FullyReviewed);
        string prompt = GetUserPrompt(handler.RequestBodies[0]);
        Assert.Contains("public static class RemovedApi", prompt);
        Assert.Contains("surviving references", prompt);
        Assert.Contains("deployment", prompt);
        Assert.Contains("deleted (baseline content shown)", prompt);
    }

    [Fact]
    public async Task SkipsBinaryEmptyMissingAndRangelessFilesWithoutModelCalls()
    {
        var handler = new StubHttpMessageHandler();
        FileReviewer reviewer = CreateReviewer(handler);

        File.WriteAllBytes(Path.Combine(_tree.Path, "blob.bin"), [0x00, 0x01, 0x02]);
        FileReviewResult binary = await reviewer.ReviewAsync(new ChangedFile("blob.bin", ChangeKind.Modified, [new LineRange(1, 1)]));
        Assert.Contains("binary", binary.SkipReason);
        Assert.Equal(FileReviewStatus.NotReviewable, binary.Status);

        File.WriteAllText(Path.Combine(_tree.Path, "empty.cs"), "");
        FileReviewResult empty = await reviewer.ReviewAsync(new ChangedFile("empty.cs", ChangeKind.Added, []));
        Assert.Contains("empty", empty.SkipReason);
        Assert.Equal(FileReviewStatus.NotReviewable, empty.Status);

        FileReviewResult missing = await reviewer.ReviewAsync(new ChangedFile("gone.cs", ChangeKind.Modified, [new LineRange(1, 1)]));
        Assert.Contains("not present", missing.SkipReason);
        Assert.Equal(FileReviewStatus.Failed, missing.Status);

        WriteLines("renamed.cs", "unchanged content");
        FileReviewResult rangeless = await reviewer.ReviewAsync(new ChangedFile("renamed.cs", ChangeKind.Renamed, []));
        Assert.Contains("no line changes", rangeless.SkipReason);
        Assert.Equal(FileReviewStatus.NotReviewable, rangeless.Status);

        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task SkipsOversizedFileWithExplicitReason()
    {
        File.WriteAllText(Path.Combine(_tree.Path, "large.cs"), new string('x', 1025));
        var handler = new StubHttpMessageHandler();
        var reviewer = new FileReviewer(CreateLoop(handler), _tree.Path, "system prompt", 800, 100000, 0, 1024);

        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile("large.cs", ChangeKind.Modified, [new LineRange(1, 1)]));

        Assert.Contains("maxFileBytes", result.SkipReason);
        Assert.Equal(FileReviewStatus.NotReviewable, result.Status);
        Assert.Empty(handler.RequestBodies);
    }

    [Fact]
    public async Task SkipsSymbolicLinkWithExplicitReason()
    {
        using var outside = new TempDirectory();
        string target = Path.Combine(outside.Path, "target.cs");
        string link = Path.Combine(_tree.Path, "linked.cs");
        File.WriteAllText(target, "class Secret { }");
        bool created = TryCreateFileSymbolicLink(link, target);
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        try
        {
            var handler = new StubHttpMessageHandler();
            FileReviewResult result = await CreateReviewer(handler).ReviewAsync(new ChangedFile("linked.cs", ChangeKind.Modified, [new LineRange(1, 1)]));

            Assert.Contains("symbolic link", result.SkipReason);
            Assert.Equal(FileReviewStatus.NotReviewable, result.Status);
            Assert.Empty(handler.RequestBodies);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public async Task RetriesFailedCallsThenSkips()
    {
        WriteLines("flaky.cs", "class Flaky { }");
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        FileReviewResult result = await CreateReviewer(handler).ReviewAsync(new ChangedFile("flaky.cs", ChangeKind.Modified, [new LineRange(1, 1)]));

        Assert.False(result.FullyReviewed);
        Assert.Equal(FileReviewStatus.Failed, result.Status);
        Assert.Null(result.Findings);
        Assert.Contains("failed after 2 retries", result.SkipReason);
        Assert.Equal(3, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task RetrySucceedsOnSecondAttempt()
    {
        WriteLines("flaky.cs", "class Flaky { }");
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("fine on retry"));

        FileReviewResult result = await CreateReviewer(handler).ReviewAsync(new ChangedFile("flaky.cs", ChangeKind.Modified, [new LineRange(1, 1)]));

        Assert.True(result.FullyReviewed);
        Assert.Contains("fine on retry", result.Findings);
    }

    [Fact]
    public async Task OversizedFileIsChunkedWithPerChunkRangesAndRealLineNumbers()
    {
        WriteLines("big.cs", Enumerable.Range(1, 120).Select(i => $"var x{i} = {i};").ToArray());
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("part one finding"));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("part two finding"));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("part three finding"));

        var reviewer = new FileReviewer(CreateLoop(handler), _tree.Path, "system prompt", 50, 1000000, 0);
        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile("big.cs", ChangeKind.Modified, [new LineRange(10, 12), new LineRange(60, 62), new LineRange(110, 112)]));

        Assert.True(result.FullyReviewed);
        Assert.Equal(3, result.TotalChunks);
        Assert.Contains("### Part 1 of 3", result.Findings);
        Assert.Contains("part three finding", result.Findings);

        string secondPrompt = GetUserPrompt(handler.RequestBodies[1]);
        Assert.Contains("60-62", secondPrompt);
        Assert.DoesNotContain("10-12", secondPrompt);
        Assert.Contains("    51 | var x51 = 51;", secondPrompt);
        Assert.Contains("part 2 of 3", secondPrompt);
    }

    [Fact]
    public async Task ChunkedReviewUsesTheHighestParsedCandidateSeverity()
    {
        WriteLines("big.cs", Enumerable.Range(1, 100).Select(number => $"var x{number} = {number};").ToArray());
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("""
            Low finding.
            ```json
            { "findings": [ { "file": "big.cs", "line": 1, "severity": "low", "summary": "low" } ] }
            ```
            """));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("""
            Critical finding.
            ```json
            { "findings": [ { "file": "big.cs", "line": 75, "severity": "critical", "summary": "critical" } ] }
            ```
            """));

        var reviewer = new FileReviewer(CreateLoop(handler), _tree.Path, "system prompt", 50, 1000000, 0);

        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile("big.cs", ChangeKind.Modified, [new LineRange(1, 100)]));

        Assert.True(result.CandidateSeverityDetermined);
        Assert.Equal(Severity.Critical, result.CandidateSeverity);
    }

    [Fact]
    public async Task OneUnparseableChunkMakesCandidateSeverityUndetermined()
    {
        WriteLines("big.cs", Enumerable.Range(1, 100).Select(number => $"var x{number} = {number};").ToArray());
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("""
            Low finding.
            ```json
            { "findings": [ { "file": "big.cs", "line": 1, "severity": "low", "summary": "low" } ] }
            ```
            """));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("prose without the required JSON"));

        var reviewer = new FileReviewer(CreateLoop(handler), _tree.Path, "system prompt", 50, 1000000, 0);

        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile("big.cs", ChangeKind.Modified, [new LineRange(1, 100)]));

        Assert.False(result.CandidateSeverityDetermined);
        Assert.Equal(Severity.Low, result.CandidateSeverity);
    }

    [Fact]
    public async Task ChunkFailureKeepsEarlierPartsAsPartialResult()
    {
        WriteLines("big.cs", Enumerable.Range(1, 100).Select(i => $"var x{i} = {i};").ToArray());
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("first part ok"));
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        var reviewer = new FileReviewer(CreateLoop(handler), _tree.Path, "system prompt", 50, 1000000, 0);
        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile("big.cs", ChangeKind.Modified, [new LineRange(1, 100)]));

        Assert.False(result.FullyReviewed);
        Assert.Equal(FileReviewStatus.Partial, result.Status);
        Assert.Contains("first part ok", result.Findings);
        Assert.Equal(1, result.CompletedChunks);
        Assert.Equal(2, result.TotalChunks);
        Assert.Contains("part 2 of 2 failed", result.SkipReason);
    }

    private FileReviewer CreateReviewer(StubHttpMessageHandler handler) => new(CreateLoop(handler), _tree.Path, "system prompt", 800, 100000, 2);

    private ToolCallLoop CreateLoop(StubHttpMessageHandler handler) => new(new ModelClient(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5)), new ReadFileLinesTool(_tree.Path), 100000);

    private void WriteLines(string relativePath, params string[] lines)
    {
        string fullPath = Path.Combine(_tree.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllLines(fullPath, lines);
    }

    private static string GetUserPrompt(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
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
