using System.Net;
using System.Text.Json;

namespace SlimShady.Tests;

public sealed class SecondOpinionReviewerTests : IDisposable
{
    private readonly TempDirectory _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void SmallFileExcerptIsTheWholeNumberedFile()
    {
        string[] lines = [.. Enumerable.Range(1, 20).Select(i => $"line {i}")];
        string excerpt = SecondOpinionReviewer.BuildCodeExcerpt(lines, [new LineRange(5, 6)], 100000, 30);

        Assert.Contains("     1 | line 1", excerpt);
        Assert.Contains("    20 | line 20", excerpt);
        Assert.DoesNotContain("omitted", excerpt);
    }

    [Fact]
    public void OversizedFileExcerptCoversRangesWithContextAndElision()
    {
        string[] lines = [.. Enumerable.Range(1, 2000).Select(i => $"line {i} with some padding text to add bulk")];
        string excerpt = SecondOpinionReviewer.BuildCodeExcerpt(lines, [new LineRange(100, 102), new LineRange(1500, 1501)], 20000, 30);

        Assert.Contains("    70 | ", excerpt);
        Assert.Contains("   100 | line 100", excerpt);
        Assert.Contains("  1500 | line 1500", excerpt);
        Assert.Contains("... (lines omitted) ...", excerpt);
        Assert.DoesNotContain("| line 700 ", excerpt);
    }

    [Fact]
    public void ContextWindowIsConfigurable()
    {
        string[] lines = [.. Enumerable.Range(1, 2000).Select(i => $"line {i} with some padding text to add bulk")];
        string excerpt = SecondOpinionReviewer.BuildCodeExcerpt(lines, [new LineRange(100, 102)], 20000, 5);

        Assert.Contains("    95 | line 95", excerpt);
        Assert.DoesNotContain("| line 94 ", excerpt);
        Assert.Contains("   107 | line 107", excerpt);
        Assert.DoesNotContain("| line 108 ", excerpt);
    }

    [Fact]
    public void OverlappingRangesMergeIntoOneWindow()
    {
        string[] lines = [.. Enumerable.Range(1, 2000).Select(i => $"line {i} with some padding text to add bulk")];
        string excerpt = SecondOpinionReviewer.BuildCodeExcerpt(lines, [new LineRange(100, 102), new LineRange(110, 112)], 20000, 30);

        Assert.DoesNotContain("... (lines omitted) ...", excerpt);
        Assert.Contains("   105 | line 105", excerpt);
    }

    [Fact]
    public async Task ValidateSendsFindingsAndCodeAndReturnsValidationText()
    {
        File.WriteAllLines(Path.Combine(_tree.Path, "Foo.cs"), ["class Foo", "{", "    int x;", "}"]);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("CONFIRMED FINDINGS\n1. real issue\nDISCARDED FINDINGS\n(none)\nVERDICT ok"));

        SecondOpinionReviewer reviewer = CreateReviewer(handler);
        var localResult = new FileReviewResult(new ChangedFile("Foo.cs", ChangeKind.Modified, [new LineRange(3, 3)]), "The local model thinks x is unused.", 1, 1, null);

        string? validation = await reviewer.ValidateAsync(localResult);

        Assert.NotNull(validation);
        Assert.Contains("CONFIRMED FINDINGS", validation);
        using var request = JsonDocument.Parse(handler.RequestBodies[0]);
        string userPrompt = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("The local model thinks x is unused.", userPrompt);
        Assert.Contains("     3 |     int x;", userPrompt);
        Assert.False(request.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task FailedCallReturnsNullInsteadOfThrowing()
    {
        File.WriteAllLines(Path.Combine(_tree.Path, "Foo.cs"), ["class Foo { }"]);
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");

        SecondOpinionReviewer reviewer = CreateReviewer(handler);
        var localResult = new FileReviewResult(new ChangedFile("Foo.cs", ChangeKind.Modified, [new LineRange(1, 1)]), "findings", 1, 1, null);

        Assert.Null(await reviewer.ValidateAsync(localResult));
    }

    [Fact]
    public async Task UnreadableCodeStillValidatesWithAnExplicitNote()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("UNVERIFIABLE"));

        SecondOpinionReviewer reviewer = CreateReviewer(handler);
        var localResult = new FileReviewResult(new ChangedFile("missing.cs", ChangeKind.Modified, [new LineRange(1, 1)]), "findings", 1, 1, null);

        Assert.NotNull(await reviewer.ValidateAsync(localResult));
        using var request = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Contains("the code could not be read", request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    private SecondOpinionReviewer CreateReviewer(StubHttpMessageHandler handler, bool enableToolCalls = false) => new(new ModelClient(new HttpClient(handler), "https://api.example.test/v1", "frontier-1", TimeSpan.FromSeconds(5), "key"), _tree.Path, "validation system prompt", 100000, 30, enableToolCalls, 5);
}
